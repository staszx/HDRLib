# Contributing to HDRLib

Thank you for considering contributing to HDRLib!

## How to Contribute

1. **Fork** the repository
2. **Create a feature branch** (`git checkout -b feature/my-feature`)
3. **Commit your changes** with clear, descriptive messages
4. **Run the tests** (`dotnet test HDRLib.Tests`)
5. **Push** to your branch (`git push origin feature/my-feature`)
6. Open a **Pull Request**

## Code Style

- Follow the existing code style (file-scoped namespaces, `var` usage, etc.)
- All public API members must have XML documentation comments
- Keep methods focused and reasonably short
- Avoid unnecessary comments; prefer self-documenting code

## Testing

- All new features must include unit tests
- Tests are written using NUnit 3
- Integration tests that require sample images go in `HdrProcessingTests.cs`
- Unit tests for individual components go in their own test files

## Pull Request Checklist

- [ ] Code builds without warnings (excluding pre-existing ones)
- [ ] All tests pass
- [ ] New code includes tests
- [ ] Documentation updated if public API changes
- [ ] Copyright header added to new files

## License

By contributing, you agree that your contributions will be licensed under the
GNU Affero General Public License v3.0 (AGPL-3.0).
