namespace NewsReview;

sealed class SetupConflictException() : Exception("A password is already configured.");
sealed class SetupTokenException() : Exception("Invalid setup token.");

/// <summary>
/// data/auth.json and this run's console-only setup token. The password code itself is PasswordAuth, shared word for
/// word with the other apps; this class only keeps the path and token in one service for the endpoints and tests.
/// </summary>
sealed class PasswordStore(string path)
{
    readonly Lock gate = new();
    // Remembered rather than checked per request, so a deleted data folder reports storage unavailable, not signed out.
    bool configured = PasswordAuth.IsConfigured(path);

    public string? SetupToken { get; private set; } = PasswordAuth.IsConfigured(path) ? null : PasswordAuth.GenerateSetupToken();
    public bool Configured => configured;

    public void Setup(string? token, string? password, string? confirmation)
    {
        lock (gate)
        {
            if (configured || PasswordAuth.IsConfigured(path) || SetupToken is null) throw new SetupConflictException();
            if (!PasswordAuth.SetupTokenMatches(SetupToken, token)) throw new SetupTokenException();
            if (PasswordAuth.ValidateNewPassword(password, confirmation) is { } error) throw new ValidationException(error);
            PasswordAuth.Save(path, password!);
            configured = true;
            SetupToken = null;
        }
    }

    /// <summary>Checks the password; on success, a verifier with fewer than 600,000 iterations is saved again once.</summary>
    public bool Verify(string? password, out bool upgraded)
    {
        upgraded = false;
        if (!PasswordAuth.Verify(path, password, out var needsRehash)) return false;
        if (needsRehash)
        {
            lock (gate) PasswordAuth.Save(path, password!);
            upgraded = true;
        }
        return true;
    }
}
