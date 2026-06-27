# Coding Style Guide

## Naming conventions

| Element | Style | Example |
|---------|-------|---------|
| Local variables | camelCase | `var taskId = ...` |
| Method parameters | camelCase | `string conversationId` |
| Private fields | camelCase, no prefix | `private readonly ILogger logger;` |
| Public properties | PascalCase | `public string Name { get; }` |
| Public methods | PascalCase | `public Task ExecuteAsync(...)` |
| Private methods | PascalCase | `private void DrainMessages()` |
| Constants | PascalCase | `private const int MaxRounds = 200;` |
| Static readonly fields | camelCase, no prefix | `private static readonly FrozenSet<string> excludedTools;` |
| Interfaces | PascalCase with `I` prefix | `public interface ITodoStore` |
| Enums | PascalCase | `public enum TodoStatus` |
| Enum values | PascalCase | `Completed, InProgress` |
| Type parameters | `T` prefix | `Channel<TMessage>` |

## Instance member access

Always use `this.` when accessing instance fields, properties, or methods:

```csharp
// Good
this.logger.LogInformation("Starting");
this.ProcessAsync(this.store);

// Bad
logger.LogInformation("Starting");
ProcessAsync(store);
```

This makes it immediately clear whether a variable is local or instance-scoped.

## Code blocks

Always use curly braces for `if`, `else`, `while`, `for`, `foreach`, `using`, even for single-line bodies:

```csharp
// Good
if (result is null)
{
    return;
}

foreach (var item in items)
{
    Process(item);
}

// Bad
if (result is null)
    return;

foreach (var item in items)
    Process(item);
```

## Naming semantics

Names must clearly reflect purpose. The code should read like prose — no comments needed to understand what a variable holds or what a method does.

```csharp
// Good — intent is obvious
var completedTaskCount = tasks.Count(t => t.State == TaskState.Completed);
var parentConversationId = task.ParentConversation;
bool isFirstRound = round == 0;

// Bad — requires context to understand
var cnt = tasks.Count(t => t.State == TaskState.Completed);
var id = task.ParentConversation;
bool flag = round == 0;
```

Methods: use verb phrases that describe the action (`BuildSystemPrompt`, `DrainInjectedMessages`, `TryAcquireSlot`). Avoid generic names like `Process`, `Handle`, `Do` unless the context makes the meaning obvious.

Boolean properties/variables: use `is`, `has`, `can`, `should` prefixes (`isRunning`, `hasAvailableSlot`, `canResume`).

## File organization

- **One type per file.** Each class, enum, struct, record, and interface must live in its own file named after the type (`MyClass.cs`, `MyEnum.cs`). Do not group multiple types in a shared file.

## General style

- **One statement per line.** No `if (x) return;` on one line.
- **Prefer `var`** when the type is obvious from the right side.
- **Use file-scoped namespaces** (`namespace Foo;` not `namespace Foo { ... }`).
- **Use raw string literals** (`"""..."""`) for multi-line strings and JSON.
- **Use collection expressions** (`[1, 2, 3]`) where applicable.
- **Use pattern matching** (`is not null`, `is { Count: > 0 }`) over null checks.
- **Use `sealed`** on classes that are not designed for inheritance.
- **Use `readonly`** on fields that are never reassigned after construction.

## Logging

- Use source-generated `[LoggerMessage]` on `partial` classes.
- Structured logging — pass parameters, don't interpolate into the message template.
- Log levels: Debug for internal flow, Information for business events, Warning for recoverable issues, Error for failures.

## Async

- Suffix async methods with `Async`.
- Always use `ConfigureAwait(false)` in library/agent code.
- Prefer `ValueTask` for hot paths that often complete synchronously.

## Tests

- Test method naming: `Method_Condition_Expected` with underscores.
- CA1707 (underscore naming) is suppressed in test projects via `Directory.Build.props`.
- Use `NSubstitute` for mocks, `xUnit` for assertions.
- Each test file mirrors the production class it tests.

## Applying to existing code

This style applies to **all new code**. Existing code should be updated gradually — when modifying a file, apply the style to the changed sections. Do not reformat entire files in unrelated commits.
