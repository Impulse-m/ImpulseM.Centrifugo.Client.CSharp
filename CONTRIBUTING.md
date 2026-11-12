# Contributing to the ImpulseM Centrifuge C# SDK

Thank you for your interest in contributing to the ImpulseM Centrifuge C# SDK!

## Development Setup

### Prerequisites

- .NET 10.0 SDK
- Docker, for the integration tests (optional for unit tests)
- Git
- A code editor (Visual Studio, VS Code, or Rider recommended)

### Getting Started

1. Fork the repository
2. Clone your fork:
   ```bash
   git clone https://github.com/YOUR_USERNAME/ImpulseM.Centrifugo.Client.CSharp.git
   cd ImpulseM.Centrifugo.Client.CSharp
   ```
3. Create a branch for your changes:
   ```bash
   git checkout -b feature/your-feature-name
   ```

### Building

```bash
# Restore dependencies
dotnet restore

# Build the project
dotnet build

# Run unit tests (no server needed)
dotnet test --filter "FullyQualifiedName!~IntegrationTests"

# Run a specific test
dotnet test --filter "FullyQualifiedName~ClientTests"
```

## Code Style

- Follow standard C# coding conventions
- Use meaningful variable and method names
- Add XML documentation comments for public APIs
- Keep methods focused and single-purpose
- Use async/await for asynchronous operations
- Ensure code is thread-safe where applicable
- Every new public member is listed in `src/Centrifugal.Centrifuge/PublicAPI.Unshipped.txt`. The build fails when one is missing.

### Example Code Style

```csharp
/// <summary>
/// Sends one rpc call and returns its result.
/// </summary>
/// <param name="method">The rpc method name.</param>
/// <param name="data">The request payload.</param>
/// <param name="cancellationToken">Stops the wait of the caller. The call may still run on the server.</param>
/// <returns>A task with the result of the call.</returns>
/// <exception cref="CentrifugeException">Thrown when the call fails.</exception>
public Task<CentrifugeRpcResult> RpcAsync(string method, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
{
    // Implementation
}
```

## Testing

### Running Tests

```bash
# Run unit tests
dotnet test --filter "FullyQualifiedName!~IntegrationTests"

# Run all tests, including the integration tests (requires Centrifugo on localhost:8000)
docker compose up -d --wait
dotnet test

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

The integration tests run against the Centrifugo image of `docker-compose.yml`. The release workflow runs the full suite before it publishes anything.

### Writing Tests

- Write unit tests for all new features
- Use xUnit for test framework
- Follow AAA pattern (Arrange, Act, Assert)
- Name tests descriptively: `MethodName_Scenario_ExpectedBehavior`
- Do not use `Thread.Sleep` or `Task.Delay` to order events. Use the fake step timers and the fake server of the test project.

Example:

```csharp
[Fact]
public void Connect_WithValidEndpoint_ChangesStateToConnecting()
{
    // Arrange
    var client = new CentrifugeClient("ws://localhost:8000/connection/websocket");

    // Act
    client.Connect();

    // Assert
    Assert.Equal(CentrifugeClientState.Connecting, client.State);
}
```

## Pull Request Process

1. Update documentation if needed
2. Add tests for new functionality
3. Ensure all tests pass
4. Update CHANGELOG.md with your changes
5. Create a pull request with a clear description

A breaking change of the public API is named in the title or the body of the pull request. The release notes are generated from the merged pull requests, so a change that is not named there does not reach them.

### PR Description Template

```markdown
## Description
Brief description of the changes

## Type of Change
- [ ] Bug fix
- [ ] New feature
- [ ] Breaking change
- [ ] Documentation update

## Testing
Description of testing performed

## Checklist
- [ ] Code follows project style guidelines
- [ ] Self-review completed
- [ ] Comments added for complex code
- [ ] Documentation updated
- [ ] Tests added/updated
- [ ] All tests pass
- [ ] No new warnings introduced
```

## Reporting Issues

### Bug Reports

When filing a bug report, please include:

- Clear title and description
- Steps to reproduce
- Expected vs actual behavior
- Code sample if applicable
- Environment details (.NET version, OS, etc.)

Never include a connection token, a connection instance or a header value in an issue. Replace them with placeholders.

### Feature Requests

For feature requests, please include:

- Clear description of the feature
- Use cases and motivation
- Example API if applicable
- Willingness to contribute

## Code of Conduct

- Be respectful and inclusive
- Welcome newcomers
- Focus on what is best for the community
- Show empathy towards other community members

## License

By contributing, you agree that your contributions will be licensed under the MIT License.

## Questions?

- Open an issue for general questions
- Check existing issues and documentation first
- Join the [Centrifugal community](https://centrifugal.dev/docs/getting-started/community)

Thank you for contributing!
