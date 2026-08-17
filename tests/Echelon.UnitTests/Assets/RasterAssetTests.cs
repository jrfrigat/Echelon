using Xunit;

namespace Echelon.UnitTests.Assets;

/// <summary>
/// Guards the raster assets that ship inside the NuGet packages and the container images against a
/// malformed PNG.
/// </summary>
/// <remarks>
/// 0.1.0 published a package icon whose <c>IDAT</c> chunk declared 4096 bytes while carrying 4550.
/// Nothing on the way in decodes an icon: the file passes a signature check, <c>dotnet pack</c>
/// embeds it, and nuget.org serves it, so the first sign of trouble was a broken picture on a
/// published page - and a published version can never be replaced. A lenient decoder had stopped at
/// row 71 of 128, a strict one refused the file outright, and the build stayed green throughout.
/// Verifying every chunk's CRC is what catches a declared length that disagrees with its payload.
/// The parsing is by hand because <c>System.Drawing</c> is Windows-only and CI runs on Linux.
/// </remarks>
public class RasterAssetTests
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void EveryShippedPng_ParsesToItsFinalByte()
    {
        var failures = new List<string>();

        foreach (var file in ShippedPngs())
        {
            var error = Validate(File.ReadAllBytes(file));
            if (error is not null)
                failures.Add($"{Path.GetRelativePath(RepositoryRoot(), file)}: {error}");
        }

        Assert.True(failures.Count == 0, $"Malformed PNG assets:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    [Fact]
    public void ThePackageIcon_IsWhatNuGetAccepts()
    {
        var path = Path.Combine(RepositoryRoot(), "build", "package-icon.png");
        Assert.True(File.Exists(path), $"PackageIcon points at build/package-icon.png, which is missing: {path}");

        var bytes = File.ReadAllBytes(path);
        Assert.Null(Validate(bytes));

        // nuget.org rejects an icon over 1 MB and renders it at 128x128; anything else is upscaled or
        // squeezed on the package page.
        Assert.True(bytes.Length <= 1024 * 1024, $"The icon is {bytes.Length} bytes; nuget.org allows at most 1 MB.");
        var (width, height) = Dimensions(bytes);
        Assert.Equal(128, width);
        Assert.Equal(128, height);
    }

    /// <summary>Returns a description of the first structural fault, or null when the file is a well-formed PNG.</summary>
    private static string? Validate(byte[] png)
    {
        if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            return "not a PNG (bad signature)";

        var offset = Signature.Length;
        var sawImageData = false;

        while (true)
        {
            if (offset + 12 > png.Length)
                return $"ran out of file at byte {offset} before an IEND chunk";

            var length = ReadUInt32(png, offset);
            if (length > int.MaxValue || offset + 12L + length > png.Length)
                return $"chunk at byte {offset} declares {length} bytes, which runs past the end of the file";

            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (offset == Signature.Length && type != "IHDR")
                return $"the first chunk is {type}, not IHDR";

            // The CRC covers the type and the data, not the length - which is exactly why a length
            // that disagrees with its payload shows up here and nowhere else.
            var stored = ReadUInt32(png, offset + 8 + (int)length);
            var computed = Crc32(png.AsSpan(offset + 4, 4 + (int)length));
            if (stored != computed)
                return $"chunk {type} at byte {offset} has CRC {stored:x8}, computed {computed:x8} over the {length} bytes it declares";

            sawImageData |= type == "IDAT";
            offset += 12 + (int)length;

            if (type != "IEND")
                continue;

            if (offset != png.Length)
                return $"IEND ends at byte {offset}, but the file is {png.Length} bytes";

            return sawImageData ? null : "no IDAT chunk: the file carries no image data";
        }
    }

    private static (int Width, int Height) Dimensions(byte[] png) =>
        ((int)ReadUInt32(png, 16), (int)ReadUInt32(png, 20));

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>The PNGs that leave the repository: the package icon and everything served by a host.</summary>
    private static IEnumerable<string> ShippedPngs()
    {
        var root = RepositoryRoot();
        var files = new List<string>();

        foreach (var directory in new[] { Path.Combine(root, "assets"), Path.Combine(root, "build") })
            if (Directory.Exists(directory))
                files.AddRange(Directory.EnumerateFiles(directory, "*.png", SearchOption.AllDirectories));

        var src = Path.Combine(root, "src");
        if (Directory.Exists(src))
            files.AddRange(Directory.EnumerateDirectories(src, "wwwroot", SearchOption.AllDirectories)
                .SelectMany(w => Directory.EnumerateFiles(w, "*.png", SearchOption.AllDirectories)));

        // A wwwroot under bin or obj is a copy of one already checked, and publishing may leave stale
        // ones behind; asserting on those reports a fault nobody can fix in source.
        var built = new[] { $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}" };
        var shipped = files.Where(f => !built.Any(f.Contains)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.NotEmpty(shipped);
        return shipped;
    }

    /// <summary>Walks up from the test assembly to the repo root (the folder holding the slnx).</summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("Echelon.slnx").Any())
            dir = dir.Parent;

        Assert.True(dir is not null, "Could not locate the repository root (Echelon.slnx) from the test assembly.");
        return dir!.FullName;
    }
}
