namespace AppSupervisor.Twitch;

/// <summary>Identifies a stored Twitch session that requires fresh broadcaster consent.</summary>
internal sealed class TwitchReauthorizationRequiredException(
    string message,
    Exception? innerException = null)
    : InvalidOperationException(message, innerException);
