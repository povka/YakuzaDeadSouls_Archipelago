namespace YakuzaDeadSouls.Ps3;

// Two independent paths to a toast, neither on the PS3MAPI control socket:
//   CCAPI    port 6333, honours icons but not sound, and is often not running
//   webMAN   port 80,   honours sound but always draws the plain info icon
public sealed class Notifier(string host)
{
    public const int CcapiPort = 6333;
    public const int MaxLength = 199;

    public enum Icon
    {
        Info = 0,
        Friend = 2,
        Trophy = 12,
    }

    // 1-3 are the physical console buzzer, not UI sounds. Deliberately absent.
    public enum Sound
    {
        None = -1,
        Trophy = 5,
    }

    public enum Channel
    {
        None,
        Ccapi,
        WebMan,
    }

    // One client for the lifetime of the process; a new HttpClient per toast
    // exhausts sockets once messages arrive in bulk.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public Channel Active { get; private set; } = Channel.None;

    private static string Trim(string message) =>
        message.Length <= MaxLength ? message : message[..(MaxLength - 1)] + "…";

    public async Task NotifyAsync(string message, Icon icon = Icon.Trophy)
    {
        var msg = Uri.EscapeDataString(Trim(message));
        await Http.GetAsync($"http://{host}:{CcapiPort}/ccapi/notify?id={(int)icon}&msg={msg}");
    }

    public async Task NotifyWithSoundAsync(string message, Sound sound = Sound.Trophy)
    {
        var msg = Uri.EscapeDataString(Trim(message));
        var snd = sound == Sound.None ? "" : ((int)sound).ToString();
        await Http.GetAsync($"http://{host}/notify.ps3mapi?msg={msg}&icon=0&snd={snd}");
    }

    public Task SendAsync(string message) => Active switch
    {
        Channel.Ccapi => NotifyAsync(message),
        Channel.WebMan => NotifyWithSoundAsync(message),
        _ => Task.CompletedTask,
    };

    // webMAN is preferred because PS3MAPI already requires it, so notifications
    // cost the player no extra setup. It also carries sound, which CCAPI does
    // not - and CCAPI's only advantage, a trophy icon instead of the info icon,
    // is not worth a second install.
    public async Task<Channel> DetectAsync()
    {
        if (await ReachableAsync($"http://{host}/", 3))
            return Active = Channel.WebMan;
        if (await ReachableAsync($"http://{host}:{CcapiPort}/ccapi/notify?id=0&msg=", 3))
            return Active = Channel.Ccapi;
        return Active = Channel.None;
    }

    private static async Task<bool> ReachableAsync(string url, int seconds)
    {
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var r = await Http.GetAsync(url, cancel.Token);
            return r.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task<bool> IsCcapiAvailableAsync() =>
        ReachableAsync($"http://{host}:{CcapiPort}/ccapi/notify?id=0&msg=", 3);
}
