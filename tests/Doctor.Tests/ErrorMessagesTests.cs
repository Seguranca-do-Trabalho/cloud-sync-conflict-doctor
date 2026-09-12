using Doctor.Gui;
using Xunit;

namespace Doctor.Tests;

/// <summary>
/// G2 (t_87f625aa) — standard §37 error messages as a CENTRALIZED resource
/// (src/Doctor.Gui/ErrorMessages.cs): no technical jargon, no user blame.
/// Rules verified: determinism (same constant instance), absence of
/// technical/blaming vocabulary, and absence of destructive vocabulary
/// (the GUIVM-02 guard also scans this file).
/// </summary>
public class ErrorMessagesTests
{
    private static readonly string[] ForbiddenJargon =
    [
        "exceção", "exception", "stack", "erro interno", "código de erro",
        "null", "api", "timeout", "log de", "0x",
    ];

    private static readonly string[] ForbiddenBlame =
    [
        "inválido", "inválida", "você errou", "digitou errado", "esqueceu",
        "culpa sua", "proibido",
    ];

    private static readonly (string Name, string Value)[] AllMessages =
    [
        ("FolderUnavailable", ErrorMessages.FolderUnavailable),
        ("NoItemSelected", ErrorMessages.NoItemSelected),
        ("MoveNotCompleted", ErrorMessages.MoveNotCompleted),
        ("UnexpectedFailure", ErrorMessages.UnexpectedFailure),
    ];

    [Fact]
    public void Every_message_exists_and_is_not_empty()
    {
        foreach (var (name, value) in AllMessages)
        {
            Assert.False(string.IsNullOrWhiteSpace(value),
                $"Error message {name} is empty.");
        }
    }

    [Fact]
    public void Messages_are_deterministic_constants()
    {
        // Same reference across distinct calls: no local time, locale, or
        // randomness in text formation (determinism §3).
        Assert.Same(ErrorMessages.FolderUnavailable, ErrorMessages.FolderUnavailable);
        Assert.Same(ErrorMessages.UnexpectedFailure, ErrorMessages.UnexpectedFailure);
    }

    [Fact]
    public void Messages_do_not_use_technical_jargon()
    {
        foreach (var (name, value) in AllMessages)
        {
            foreach (var term in ForbiddenJargon)
            {
                Assert.DoesNotContain(term, value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Messages_do_not_blame_the_user()
    {
        foreach (var (name, value) in AllMessages)
        {
            foreach (var term in ForbiddenBlame)
            {
                Assert.DoesNotContain(term, value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Move_error_always_references_quarantine_as_safe_destination()
    {
        // §18/§37: even on error, the user knows nothing is discarded.
        Assert.Contains("quarantine", ErrorMessages.MoveNotCompleted,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("intact", ErrorMessages.UnexpectedFailure,
            StringComparison.OrdinalIgnoreCase);
    }
}
