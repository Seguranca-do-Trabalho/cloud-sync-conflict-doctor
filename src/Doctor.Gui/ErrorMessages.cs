namespace Doctor.Gui;

/// <summary>
/// Default GUI error messages (G2 — t_87f625aa; §37):
/// plain language, no technical jargon, no user blame.
/// CENTRALIZED resource: any screen that needs to report a failure uses these
/// texts, never its own literal. Deterministic constants (no local time,
/// no locale, no randomness) and no destructive vocabulary (the GUIVM-02 guard
/// scans this file along with the screens).
/// </summary>
public static class ErrorMessages
{
    /// <summary>The chosen folder could not be read (nonexistent, no permission, or offline).</summary>
    public const string FolderUnavailable =
        "We couldn't access this folder right now. Check if it exists and try again.";

    /// <summary>No item was selected before a queue action.</summary>
    public const string NoItemSelected =
        "Choose at least one item from the list before continuing.";

    /// <summary>The move to quarantine did not finish; nothing was lost.</summary>
    public const string MoveNotCompleted =
        "The move to quarantine did not finish. Your files are intact — try again later.";

    /// <summary>Unexpected failure; state preserved.</summary>
    public const string UnexpectedFailure =
        "Something unexpected happened and we stopped for safety. Your files remain intact.";
}
