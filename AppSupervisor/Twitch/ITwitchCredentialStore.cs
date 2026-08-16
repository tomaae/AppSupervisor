namespace AppSupervisor.Twitch;

internal interface ITwitchCredentialStore
{
    TwitchStoredAuthorization? Load();
    void Save(TwitchStoredAuthorization authorization);
    void Delete();
}
