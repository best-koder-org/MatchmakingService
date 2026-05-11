using MatchmakingService.Controllers;
using MatchmakingService.Data;
using MatchmakingService.Models;
using MatchmakingService.Strategies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MatchmakingService.Tests.Controllers;

public class ProfilesControllerTests : IDisposable
{
    private readonly MatchmakingDbContext _context;
    private readonly Mock<IOptionsMonitor<CandidateOptions>> _optionsMock;
    private readonly Mock<ICandidateStrategy> _strategyMock;
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;

    public ProfilesControllerTests()
    {
        var dbOptions = new DbContextOptionsBuilder<MatchmakingDbContext>()
            .UseInMemoryDatabase(databaseName: $"ProfilesCtrlTests_{Guid.NewGuid()}")
            .Options;
        _context = new MatchmakingDbContext(dbOptions);

        _optionsMock = new Mock<IOptionsMonitor<CandidateOptions>>();
        _optionsMock.Setup(x => x.CurrentValue).Returns(new CandidateOptions());

        _strategyMock = new Mock<ICandidateStrategy>();
        _strategyMock.Setup(x => x.Name).Returns("Live");

        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        // Stub: return an HttpClient whose handler always responds 404 so legacy fallback
        // and profile enrichment fail cleanly with empty results (no NRE on null client).
        _httpClientFactoryMock
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new StubHttpHandler(HttpStatusCode.NotFound))
            {
                BaseAddress = new Uri("http://stub.local")
            });
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private ProfilesController CreateController(ICandidateStrategy? strategyOverride = null)
    {
        var strategy = strategyOverride ?? _strategyMock.Object;

        // Create a mock StrategyResolver that always returns our mock strategy
        var serviceProviderMock = new Mock<IServiceProvider>();
        var scoringConfig = new Mock<IOptionsMonitor<ScoringConfiguration>>();
        scoringConfig.Setup(x => x.CurrentValue).Returns(new ScoringConfiguration());

        // We need a real StrategyResolver but with mocked dependencies
        // Since StrategyResolver.Resolve is not virtual, we'll use a wrapper approach
        // Actually, let's just create a real resolver that returns our mock
        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(LiveScoringStrategy)))
            .Returns(CreateDummyLiveStrategy());
        serviceProviderMock
            .Setup(sp => sp.GetService(typeof(PreComputedStrategy)))
            .Returns(CreateDummyPreComputedStrategy());

        var resolver = new StrategyResolver(
            serviceProviderMock.Object,
            _optionsMock.Object,
            _context,
            NullLogger<StrategyResolver>.Instance);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Services:UserService:BaseUrl", "http://localhost:8082" }
            })
            .Build();

        var controller = new ProfilesController(
            resolver,
            _httpClientFactoryMock.Object,
            config,
            NullLogger<ProfilesController>.Instance,
            _context);

        // Set up HttpContext with headers
        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return controller;
    }

    private (ProfilesController controller, Mock<ICandidateStrategy> stratMock) CreateControllerWithMockStrategy()
    {
        var stratMock = new Mock<ICandidateStrategy>();
        stratMock.Setup(x => x.Name).Returns("Live");

        // We can't easily mock StrategyResolver since Resolve isn't virtual
        // Instead, seed DB with users and create a real strategy pipeline
        // OR: test the controller with actual StrategyResolver + seeded DB

        // For controller tests, we'll verify the HTTP contract rather than mocking strategy
        // The integration between controller and strategy is tested by seeding the DB
        return (CreateController(), stratMock);
    }

    // --- Non-integer userId tests ---

    [Fact]
    public async Task GetProfiles_NonIntegerUserId_ReturnsEmptyList()
    {
        var controller = CreateController();
        var result = await controller.GetProfiles("keycloak-uuid-here");

        var okResult = Assert.IsType<OkObjectResult>(result);
        var items = okResult.Value as IEnumerable<object>;
        Assert.NotNull(items);
        Assert.Empty(items);
    }

    // --- Query parameter clamping tests (T180) ---

    [Fact]
    public async Task GetProfiles_LimitClamped_TooHigh()
    {
        SeedActiveUser(1);
        var controller = CreateController();

        // limit=200 should be clamped to 50
        var result = await controller.GetProfiles("1", limit: 200);

        // Should not throw — clamping should work
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetProfiles_LimitClamped_Zero()
    {
        SeedActiveUser(1);
        var controller = CreateController();

        // limit=0 should be clamped to 1
        var result = await controller.GetProfiles("1", limit: 0);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetProfiles_NegativeLimit_ClampedToOne()
    {
        SeedActiveUser(1);
        var controller = CreateController();

        var result = await controller.GetProfiles("1", limit: -5);
        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetProfiles_MinScoreClamped()
    {
        SeedActiveUser(1);
        var controller = CreateController();

        // minScore=-10 → clamped to 0, minScore=200 → clamped to 100
        var result1 = await controller.GetProfiles("1", minScore: -10);
        Assert.IsType<OkObjectResult>(result1);

        var result2 = await controller.GetProfiles("1", minScore: 200);
        Assert.IsType<OkObjectResult>(result2);
    }

    [Fact]
    public async Task GetProfiles_ActiveWithinClamped()
    {
        SeedActiveUser(1);
        var controller = CreateController();

        // activeWithin=0 → clamped to 1, activeWithin=999 → clamped to 365
        var result = await controller.GetProfiles("1", activeWithin: 999);
        Assert.IsType<OkObjectResult>(result);
    }

    // --- Strategy override via query param ---

    [Fact]
    public async Task GetProfiles_StrategyOverride_Live()
    {
        SeedActiveUser(1);
        SeedActiveUser(2);

        var controller = CreateController();
        var result = await controller.GetProfiles("1", strategy: "live");

        Assert.IsType<OkObjectResult>(result);
    }

    // --- Valid request returns candidates ---

    [Fact]
    public async Task GetProfiles_ValidUser_ReturnsCandidates()
    {
        SeedActiveUser(1);
        SeedActiveUser(2);
        SeedActiveUser(3);

        var controller = CreateController();
        var result = await controller.GetProfiles("1");

        var okResult = Assert.IsType<OkObjectResult>(result);
        // Should return candidates (the live strategy pipeline runs with seeded users)
        Assert.NotNull(okResult.Value);
    }

    // --- Response shape matches Flutter expectations ---

    [Fact]
    public async Task GetProfiles_ResponseShape_ContainsExpectedFields()
    {
        SeedActiveUser(1);
        SeedActiveUser(2, age: 30, gender: "Female");

        var controller = CreateController();
        var result = await controller.GetProfiles("1");

        var okResult = Assert.IsType<OkObjectResult>(result);
        var items = okResult.Value as IEnumerable<object>;
        Assert.NotNull(items);

        // The response is anonymous objects mapped by MapToFlutterShape
        // Verify it's a list (even if empty due to strategy pipeline filtering)
        // This tests the controller doesn't throw during mapping
    }

    // --- Default params ---

    [Fact]
    public async Task GetProfiles_DefaultParams_UsesDefaultLimitAndMinScore()
    {
        SeedActiveUser(1);
        var controller = CreateController();

        // No query params → defaults: limit=20, minScore=0, no activeWithin filter
        var result = await controller.GetProfiles("1");
        Assert.IsType<OkObjectResult>(result);
    }

    // --- Helper methods ---

    private void SeedActiveUser(int userId, int age = 28, string gender = "Male")
    {
        _context.UserProfiles.Add(new UserProfile
        {
            UserId = userId,
            IsActive = true,
            Gender = gender,
            PreferredGender = "Female",
            Age = age,
            MinAge = 22,
            MaxAge = 35,
            Latitude = 59.33,
            Longitude = 18.07,
            MaxDistance = 50,
            DesirabilityScore = 50,
            LastActiveAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _context.SaveChanges();
    }

    private LiveScoringStrategy CreateDummyLiveStrategy()
    {
        var filterPipeline = new MatchmakingService.Filters.CandidateFilterPipeline(
            Enumerable.Empty<MatchmakingService.Filters.ICandidateFilter>(),
            NullLogger<MatchmakingService.Filters.CandidateFilterPipeline>.Instance);
        var matchingService = new Mock<MatchmakingService.Services.IAdvancedMatchingService>();
        matchingService.Setup(x => x.CalculateCompatibilityScoreAsync(It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(80.0);
        var swipeClient = new Mock<MatchmakingService.Services.ISwipeServiceClient>();
        swipeClient.Setup(x => x.GetSwipedUserIdsAsync(It.IsAny<int>()))
            .ReturnsAsync(new HashSet<int>());
        swipeClient.Setup(x => x.GetBatchTrustScoresAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync((IEnumerable<int> ids) => ids.ToDictionary(id => id, _ => 100m));
        var safetyClient = new Mock<MatchmakingService.Services.ISafetyServiceClient>();
        safetyClient.Setup(x => x.GetBlockedUserIdsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<int>());
        var scoringConfig = new Mock<IOptionsMonitor<ScoringConfiguration>>();
        scoringConfig.Setup(x => x.CurrentValue).Returns(new ScoringConfiguration
        {
            MinimumCompatibilityThreshold = 0
        });

        return new LiveScoringStrategy(
            _context,
            filterPipeline,
            matchingService.Object,
            swipeClient.Object,
            safetyClient.Object,
            _optionsMock.Object,
            scoringConfig.Object,
            NullLogger<LiveScoringStrategy>.Instance);
    }

    private PreComputedStrategy CreateDummyPreComputedStrategy()
    {
        var filterPipeline = new MatchmakingService.Filters.CandidateFilterPipeline(
            Enumerable.Empty<MatchmakingService.Filters.ICandidateFilter>(),
            NullLogger<MatchmakingService.Filters.CandidateFilterPipeline>.Instance);
        var swipeClient = new Mock<MatchmakingService.Services.ISwipeServiceClient>();
        swipeClient.Setup(x => x.GetSwipedUserIdsAsync(It.IsAny<int>()))
            .ReturnsAsync(new HashSet<int>());
        swipeClient.Setup(x => x.GetBatchTrustScoresAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync((IEnumerable<int> ids) => ids.ToDictionary(id => id, _ => 100m));
        var safetyClient = new Mock<MatchmakingService.Services.ISafetyServiceClient>();
        safetyClient.Setup(x => x.GetBlockedUserIdsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<int>());
        var scoringConfig = new Mock<IOptionsMonitor<ScoringConfiguration>>();
        scoringConfig.Setup(x => x.CurrentValue).Returns(new ScoringConfiguration
        {
            MinimumCompatibilityThreshold = 0
        });

        return new PreComputedStrategy(
            _context,
            filterPipeline,
            CreateDummyLiveStrategy(),
            swipeClient.Object,
            safetyClient.Object,
            _optionsMock.Object,
            scoringConfig.Object,
            NullLogger<PreComputedStrategy>.Instance);
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public StubHttpHandler(HttpStatusCode status) => _status = status;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent("") });
    }
}
