using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace RustMonitor.Core;

// The same standard item icons linked by the official Rust Wiki. Download on
// demand to the user's cache; copyrighted images are not bundled in the release.
public sealed class ItemIcons : IDisposable
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    private readonly string directory;
    private readonly HttpClient client;
    private readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> pending = new();
    private readonly SemaphoreSlim limit = new(4);
    private readonly CancellationTokenSource shutdown = new();
    public ItemIcons(string directory, HttpClient? client = null) { this.directory = directory; this.client = client ?? Client; }
    public static Uri? ImageUri(string shortName) => Regex.IsMatch(shortName, @"\A[a-z0-9][a-z0-9._-]{0,95}\z")
        ? new Uri("https://files.facepunch.com/rust/item/" + shortName + "_512.png") : null;
    public Task<BitmapSource?> GetAsync(string shortName)
    {
        var uri = ImageUri(shortName);
        if (uri == null || shutdown.IsCancellationRequested) return Task.FromResult<BitmapSource?>(null);
        return pending.GetOrAdd(shortName, _ => new Lazy<Task<BitmapSource?>>(() => LoadAsync(shortName, uri))).Value;
    }
    private async Task<BitmapSource?> LoadAsync(string shortName, Uri uri)
    {
        var entered = false;
        try
        {
            await limit.WaitAsync(shutdown.Token).ConfigureAwait(false); entered = true;
            var path = Path.Combine(directory, shortName + ".png");
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length <= MaxBytes)
                    return Decode(await File.ReadAllBytesAsync(path, shutdown.Token).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException or OverflowException) { }
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, shutdown.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
            using var input = await response.Content.ReadAsStreamAsync(shutdown.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[16384]; int count;
            while ((count = await input.ReadAsync(buffer, shutdown.Token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > MaxBytes) return null;
                output.Write(buffer, 0, count);
            }
            var bytes = output.ToArray(); var image = Decode(bytes);
            try
            {
                Directory.CreateDirectory(directory);
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllBytesAsync(temp, bytes, shutdown.Token).ConfigureAwait(false); File.Move(temp, path, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return image;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException or OverflowException)
        { return null; }
        finally { if (entered) limit.Release(); }
    }
    private static BitmapSource Decode(byte[] bytes)
    {
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("Invalid icon");
        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is 0 or > 2048 || height is 0 or > 2048) throw new InvalidDataException("Invalid icon size");
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 128; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    public void Dispose() => shutdown.Cancel();
}
