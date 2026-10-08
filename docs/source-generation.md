# Shared source generation

ConnectX packet registration and actor handler generators embed helpers from the private [Corona.SourceGeneration](https://github.com/Corona-Studio/Corona.SourceGeneration) repository through the pinned `SourceGeneration` Git submodule. Business attributes and generated APIs remain in ConnectX/Hive.

Use `git submodule update --init --recursive` after cloning. CI's `ACCESS_TOKEN` must have read access to the private shared repository. Import `SourceGeneration.props` only in generator projects; no extra helper DLL is needed at runtime. Update the common repository first, then test and commit the new submodule pin here.

Run `dotnet test ConnectX.SourceGenerator.Tests/ConnectX.SourceGenerator.Tests.csproj`. These tests compile generated reference/value packet formatters and partial actor handlers, check invalid signatures, and verify unchanged models on unrelated compilation edits. NativeAOT's concrete MemoryPack formatter registration and validation methods are preserved.
