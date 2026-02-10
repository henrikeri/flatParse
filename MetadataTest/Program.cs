using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;

// Quick diagnostic tool to check FITS/XISF metadata
class Program
{
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: MetadataTest <flat_directory_path>");
            return;
        }

        var dir = args[0];
        if (!Directory.Exists(dir))
        {
            Console.WriteLine($"Directory not found: {dir}");
            return;
        }

        Console.WriteLine($"Scanning: {dir}\n");

        var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".fits", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".fit", StringComparison.OrdinalIgnoreCase) ||
                       f.EndsWith(".xisf", StringComparison.OrdinalIgnoreCase))
            .Take(10)
            .ToList();

        if (!files.Any())
        {
            Console.WriteLine("No FITS/XISF files found in this directory");
            return;
        }

        foreach (var file in files)
        {
            Console.WriteLine($"\n{'='} {Path.GetFileName(file)} {'='}");
            Console.WriteLine($"Size: {new FileInfo(file).Length:N0} bytes");

            if (file.EndsWith(".xisf", StringComparison.OrdinalIgnoreCase))
            {
                CheckXisf(file);
            }
            else
            {
                CheckFits(file);
            }
        }
    }

    static void CheckFits(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[2880];
            
            Console.WriteLine("\nFITS Keywords:");
            var foundExptime = false;
            var foundImagetyp = false;

            while (fs.Read(buffer, 0, 2880) == 2880)
            {
                for (int i = 0; i < 36; i++)
                {
                    var card = Encoding.ASCII.GetString(buffer, i * 80, 80);
                    
                    if (card.StartsWith("END "))
                        return;

                    var eqIndex = card.IndexOf('=');
                    if (eqIndex > 0 && eqIndex < 9)
                    {
                        var key = card[..eqIndex].Trim();
                        var rest = card[(eqIndex + 1)..];
                        var slashIndex = rest.IndexOf('/');
                        var value = (slashIndex > 0 ? rest[..slashIndex] : rest).Trim().Trim('\'', ' ');

                        if (key.Equals("EXPTIME", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("EXPOSURE", StringComparison.OrdinalIgnoreCase) ||
                            key.Equals("EXPOSURETIME", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"  {key} = {value}");
                            foundExptime = true;
                        }
                        else if (key.Equals("IMAGETYP", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("FRAMETYPE", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("FRAME", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"  {key} = {value}");
                            foundImagetyp = true;
                        }
                        else if (key.Equals("BINNING", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("XBINNING", StringComparison.OrdinalIgnoreCase) ||
                                key.Equals("FILTER", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine($"  {key} = {value}");
                        }
                    }
                }
            }

            if (!foundExptime)
                Console.WriteLine("  ⚠ No EXPTIME/EXPOSURE found!");
            if (!foundImagetyp)
                Console.WriteLine("  ⚠ No IMAGETYP/FRAMETYPE found!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
        }
    }

    static void CheckXisf(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[512 * 1024];
            var bytesRead = fs.Read(buffer, 0, buffer.Length);
            var header = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            var closeMatch = Regex.Match(header, @"</\s*(?:\w+:)?XISF\s*>", RegexOptions.IgnoreCase);
            if (closeMatch.Success)
            {
                header = header[..(closeMatch.Index + closeMatch.Length)];
            }

            Console.WriteLine("\nXISF FITSKeywords:");
            var fitsPattern = new Regex(@"<FITSKeyword\s+(?:name|keyword)=""([^""]+)""\s+value=""([^""]+)""", RegexOptions.IgnoreCase);
            
            var foundExptime = false;
            var foundImagetyp = false;

            foreach (Match match in fitsPattern.Matches(header))
            {
                var key = match.Groups[1].Value;
                var value = match.Groups[2].Value;

                if (key.Equals("EXPTIME", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("EXPOSURE", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("EXPOSURETIME", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  {key} = {value}");
                    foundExptime = true;
                }
                else if (key.Equals("IMAGETYP", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("FRAMETYPE", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("FRAME", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  {key} = {value}");
                    foundImagetyp = true;
                }
                else if (key.Equals("FILTER", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("BINNING", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  {key} = {value}");
                }
            }

            if (!foundExptime)
                Console.WriteLine("  ⚠ No EXPTIME/EXPOSURE found!");
            if (!foundImagetyp)
                Console.WriteLine("  ⚠ No IMAGETYP/FRAMETYPE found!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
        }
    }
}
