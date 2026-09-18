using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace FluidDock;

/// <summary>Where the in-app update is, from "nothing asked yet" to "restarting into the new one".</summary>
internal enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Applying,
    Failed,
}

/// <summary>
/// Updates the dock in place from the repository's GitHub releases.
///
/// The point is that an update does not touch <c>config\</c>. Before this, a new build meant
/// downloading a zip, unpacking it somewhere new and adding every icon to the dock again by hand -
/// so the fix for a bug cost more than the bug. Now the exe swaps itself under the same folder
/// and dock.json stays exactly where the settings panel has been writing it.
///
/// The swap is the rename dance Windows allows and nothing more: a running exe cannot be
/// overwritten or deleted, but it can be renamed. So the download lands next to the exe as
/// <c>FluidDock.exe.new</c>, the running one is renamed to <c>FluidDock.exe.old</c>, the new one
/// takes its name, is started, and this process quits. The new process waits for this one to go
/// (see <c>--after-update</c> in Program.Main - the single-instance mutex is still held until
/// then), and deletes the <c>.old</c> on its next start. If the second rename fails the first is
/// undone, so the worst case is the version that was already running.
///
/// The release asset is the single self-contained exe that <c>tools\Publish.ps1</c> produces -
/// deliberately the one file. The tray icon falls back to the one compiled in when
/// <c>assets\tray.ico</c> is absent (see TrayIcon), so nothing else needs shipping.
///
/// The repository is private, and a private repository's API and release assets answer 404 to
/// anyone unauthenticated. So a token is read from <c>config\github-token.txt</c>, or from the
/// <c>FLUIDDOCK_GITHUB_TOKEN</c> environment variable, when either exists; a fine-grained token
/// with read access to the repository's contents is enough. A public repository needs neither.
/// The asset is fetched through the API URL with <c>Accept: application/octet-stream</c> rather
/// than its browser URL, which is the form that honours the token; the redirect it answers with
/// is followed by hand so the token is not forwarded to the storage host, which rejects it.
///
/// Everything network-bound runs on the pool; every state change is handed back to the UI
/// thread through <paramref name="defer"/>, which is the same route the file dialogs' answers
/// take, and the panel's rows read state from here rather than being handed it - so a row that
/// was rebuilt mid-download is not a row holding a stale reference.
/// </summary>
internal sealed class Updater
{
    public const string Owner = "ShizukiR1n";
    public const string Repo = "FluidDock";
    public const string AssetName = "FluidDock.exe";
    public const string TokenFileName = "github-token.txt";
    public const string TokenVariable = "FLUIDDOCK_GITHUB_TOKEN";

    /// <summary>Overrides the "latest release" URL. For tests, which serve a release of their own.</summary>
    public const string ApiVariable = "FLUIDDOCK_RELEASES_API";

    private const int MaxRedirects = 5;

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    private readonly string _configDir;
    private readonly Action<Action> _defer;
    private readonly Action _quit;
    private readonly Action<string> _note;

    private Version? _latest;
    private string? _assetUrl;
    private long _assetSize;
    private string? _assetDigest;
    private int _percent;
    private string _error = string.Empty;

    /// <summary>The version this exe was built as. Compared against the release tag, so the tag has to be a version.</summary>
    public static Version Current { get; } = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0));

    public UpdatePhase Phase { get; private set; }

    public event Action? Changed;

    public Updater(string configDir, Action<Action> defer, Action quit, Action<string> note)
    {
        _configDir = configDir;
        _defer = defer;
        _quit = quit;
        _note = note;
    }

    /// <summary>"V0.6", or "V0.6.1" when there is a third number. The shape the release tags use.</summary>
    public static string Label(Version version) =>
        version.Build > 0 ? $"V{version.Major}.{version.Minor}.{version.Build}" : $"V{version.Major}.{version.Minor}";

    /// <summary>What the row's action text should say now.</summary>
    public string ActionText => Phase switch
    {
        UpdatePhase.Checking => "正在检查…",
        UpdatePhase.Available => $"更新到 {Label(_latest!)}",
        UpdatePhase.Downloading => "正在下载…",
        UpdatePhase.Applying => "正在重启…",
        UpdatePhase.Failed => "重试",
        _ => "检查更新",
    };

    /// <summary>What the row's detail text should say now.</summary>
    public string StatusText => Phase switch
    {
        UpdatePhase.Idle => $"当前 {Label(Current)}",
        UpdatePhase.UpToDate => $"已是最新 {Label(Current)}",
        UpdatePhase.Available => $"{_assetSize / 1048576.0:0.#} MB",
        UpdatePhase.Downloading => $"{_percent}%",
        UpdatePhase.Failed => _error,
        _ => string.Empty,
    };

    /// <summary>
    /// The row's click. What it does depends on where things are: a check when nothing is known,
    /// the download when a newer version is, nothing while either is under way.
    /// </summary>
    public void Run()
    {
        switch (Phase)
        {
            case UpdatePhase.Idle:
            case UpdatePhase.UpToDate:
            case UpdatePhase.Failed:
                Check();
                break;

            case UpdatePhase.Available:
                Download();
                break;
        }
    }

    /// <summary>
    /// Removes the previous version's exe, left behind by the swap that started this process.
    /// Quietly: if it is still exiting the delete fails, and the next start gets it.
    /// </summary>
    public static void CleanUp()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch
        {
            // Next time.
        }
    }

    private void Check()
    {
        Enter(UpdatePhase.Checking);

        _ = Task.Run(async () =>
        {
            try
            {
                (Version latest, string? url, long size, string? digest) = await FetchLatest().ConfigureAwait(false);

                _defer(() =>
                {
                    _latest = latest;
                    _assetUrl = url;
                    _assetSize = size;
                    _assetDigest = digest;

                    if (latest <= Current) Enter(UpdatePhase.UpToDate);
                    else if (url is null) Fail($"{Label(latest)} 里没有 {AssetName}");
                    else Enter(UpdatePhase.Available);

                    _note($"update check: current {Label(Current)}, latest {Label(latest)}, asset {(url is null ? "missing" : $"{size} bytes")}");
                });
            }
            catch (Exception ex)
            {
                _defer(() => Fail(Describe(ex)));
            }
        });
    }

    private void Download()
    {
        string? url = _assetUrl;
        long size = _assetSize;
        string? digest = _assetDigest;
        if (url is null) return;

        _percent = 0;
        Enter(UpdatePhase.Downloading);

        _ = Task.Run(async () =>
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
            string fresh = exe + ".new";

            try
            {
                await Fetch(url, fresh, size, digest).ConfigureAwait(false);
                _defer(() => Apply(exe, fresh));
            }
            catch (Exception ex)
            {
                TryDelete(fresh);
                _defer(() => Fail(Describe(ex)));
            }
        });
    }

    /// <summary>The swap. On the UI thread, because quitting is a PostQuitMessage and that posts to the calling thread.</summary>
    private void Apply(string exe, string fresh)
    {
        Enter(UpdatePhase.Applying);

        string old = exe + ".old";

        try
        {
            TryDelete(old);
            File.Move(exe, old);

            try
            {
                File.Move(fresh, exe);
            }
            catch
            {
                File.Move(old, exe);
                throw;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--after-update {Environment.ProcessId}",
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = false,
            })?.Dispose();

            _note($"update applied: {Label(_latest!)} started, quitting");
            _quit();
        }
        catch (Exception ex)
        {
            TryDelete(fresh);
            Fail("替换失败，已回滚");
            _note($"update apply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<(Version, string?, long, string?)> FetchLatest()
    {
        string api = Environment.GetEnvironmentVariable(ApiVariable)
            ?? $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

        using HttpClient client = MakeClient(CheckTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, api);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        Authorize(request);

        using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // A private repository looks exactly like a missing one from outside. Say which is
            // likelier, which depends on whether we had anything to show the door.
            throw new UpdateException(Token() is null ? "仓库不可见：需要 token" : "没有找到 Release");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UpdateException("token 无效或没有权限");

        response.EnsureSuccessStatusCode();

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        JsonElement root = json.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        Version latest = ParseTag(tag) ?? throw new UpdateException($"标签不是版本号：{tag}");

        string? url = null;
        long size = 0;
        string? digest = null;

        if (root.TryGetProperty("assets", out JsonElement assets))
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                url = asset.GetProperty("url").GetString();
                size = asset.GetProperty("size").GetInt64();
                if (asset.TryGetProperty("digest", out JsonElement d) && d.ValueKind == JsonValueKind.String)
                    digest = d.GetString();
                break;
            }
        }

        return (latest, url, size, digest);
    }

    /// <summary>
    /// Streams the asset to disk, reporting progress, and refuses to hand over anything whose
    /// size - or SHA-256, when the release carries one - is not what the release said.
    /// </summary>
    private async Task Fetch(string url, string path, long expectedSize, string? digest)
    {
        using HttpClient client = MakeClient(DownloadTimeout);
        using HttpResponseMessage response = await Follow(client, url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? expectedSize;
        long done = 0;
        int shown = -1;

        using SHA256 sha = SHA256.Create();

        await using (Stream body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            byte[] buffer = new byte[1 << 16];
            int read;

            while ((read = await body.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                sha.TransformBlock(buffer, 0, read, null, 0);
                done += read;

                int percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent != shown)
                {
                    shown = percent;
                    _defer(() => Progress(percent));
                }
            }

            sha.TransformFinalBlock([], 0, 0);
        }

        if (expectedSize > 0 && done != expectedSize)
            throw new UpdateException("下载不完整");

        if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            string actual = Convert.ToHexString(sha.Hash!);
            if (!string.Equals(actual, digest["sha256:".Length..], StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("下载校验失败");
        }

        // Whatever else it is, a build of this program starts with the two bytes every Windows
        // executable does. A 404 page saved as FluidDock.exe would otherwise be installed.
        await using (var check = File.OpenRead(path))
        {
            if (check.Length < 2 || check.ReadByte() != 'M' || check.ReadByte() != 'Z')
                throw new UpdateException("下载的不是程序文件");
        }
    }

    /// <summary>
    /// GETs with the token, following redirects by hand so the token stays on the API host.
    /// GitHub answers an authenticated asset request with a redirect to its storage host, and
    /// that host fails the request outright if the Authorization header arrives with it.
    /// </summary>
    private async Task<HttpResponseMessage> Follow(HttpClient client, string url)
    {
        bool first = true;

        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            if (first) Authorize(request);

            HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is null)
                return response;

            url = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location.ToString()
                : new Uri(new Uri(url), response.Headers.Location).ToString();
            response.Dispose();
            first = false;
        }

        throw new UpdateException("重定向次数过多");
    }

    private static HttpClient MakeClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"FluidDock/{Label(Current)}");
        return client;
    }

    private void Authorize(HttpRequestMessage request)
    {
        string? token = Token();
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Read fresh each time, so a token file added while the dock runs is picked up by the next check.</summary>
    private string? Token()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(TokenVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();

        try
        {
            string path = Path.Combine(_configDir, TokenFileName);
            if (File.Exists(path))
            {
                string token = File.ReadAllText(path).Trim();
                if (token.Length > 0) return token;
            }
        }
        catch
        {
            // Unreadable is the same as absent.
        }

        return null;
    }

    /// <summary>"v0.6", "V0.6.1", "0.7" - whatever precedes the first digit is dropped.</summary>
    private static Version? ParseTag(string tag)
    {
        int start = 0;
        while (start < tag.Length && !char.IsDigit(tag[start])) start++;

        int end = start;
        while (end < tag.Length && (char.IsDigit(tag[end]) || tag[end] == '.')) end++;

        string text = tag[start..end];
        if (!text.Contains('.')) text += ".0";

        return Version.TryParse(text, out Version? version) ? Normalize(version) : null;
    }

    /// <summary>Four components always, so "0.6" and "0.6.0.0" compare equal instead of the shorter one losing.</summary>
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private static string Describe(Exception ex) => ex switch
    {
        UpdateException => ex.Message,
        TaskCanceledException => "连接超时",
        HttpRequestException => "无法连接 GitHub",
        IOException => "写入文件失败",
        _ => ex.GetType().Name,
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Left for the next start's CleanUp, or for the user.
        }
    }

    private void Enter(UpdatePhase phase)
    {
        Phase = phase;
        Changed?.Invoke();
    }

    private void Progress(int percent)
    {
        if (Phase != UpdatePhase.Downloading) return;
        _percent = percent;
        Changed?.Invoke();
    }

    private void Fail(string error)
    {
        _error = error;
        _note($"update failed: {error}");
        Enter(UpdatePhase.Failed);
    }

    /// <summary>A failure with a message fit for the row, as opposed to one that needs translating.</summary>
    private sealed class UpdateException(string message) : Exception(message);
}
