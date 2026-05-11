# MatchmakingService

Core matchmaking and candidate scoring service for the DatingApp platform.

## What It Does

This service is responsible for:
- Candidate ranking and scoring
- Match decision workflows
- Compatibility-related scoring logic
- Read models for profile discovery and recommendation APIs

## Why It Is Interesting

This is one of the strongest backend examples in the platform:
- Domain-driven scoring logic rather than thin CRUD only
- CQRS-style command/query separation
- Real service boundaries in a distributed architecture
- Automated tests around controller and business behavior

## Stack

- .NET 8
- ASP.NET Core Web API
- MediatR (CQRS patterns)
- EF Core 8 + MySQL

## Project Layout

```text
MatchmakingService/
  Controllers/
  Commands/
  Queries/
  Services/
  Data/
  Models/
  DTOs/
  MatchmakingService.Tests/
```

## Build and Test

```bash
dotnet restore MatchmakingService.csproj
dotnet build MatchmakingService.csproj
dotnet test MatchmakingService.Tests/MatchmakingService.Tests.csproj
```

## Run Locally

```bash
dotnet run --project MatchmakingService.csproj
```

## Example Concerns in This Repo

- Compatibility model computation
- Candidate filtering and ordering
- Match lifecycle state transitions
- Service contracts with swipe and messaging domains

## Related Repositories

- `best-koder-org/swipe-service`
- `best-koder-org/UserService`
- `best-koder-org/dejting-yarp`

## Status

Active development repository with advanced domain logic.
