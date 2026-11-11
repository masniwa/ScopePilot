using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScopePilot.Services;

public static class ExplorationUploadFixture
{
    public static async Task<string> CreateAsync(string runDirectory)
    {
        var directory = Path.Combine(runDirectory, "upload-fixtures");
        Directory.CreateDirectory(directory);
        var marker = "ScopePilot-" + Path.GetFileName(runDirectory);
        var contents = Enumerable.Range(1, 2).Select(page => $"BT /F1 16 Tf 50 760 Td ({marker}) Tj 0 -24 Td (Page {page} of 2 - generated exploration document.) Tj ET\n").ToArray();
        var objects = new[] {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 7 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(contents[0])} >>\nstream\n{contents[0]}endstream",
            $"<< /Length {Encoding.ASCII.GetByteCount(contents[1])} >>\nstream\n{contents[1]}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i+1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var bytes = Encoding.ASCII.GetBytes(pdf.ToString());
        var file = Path.Combine(directory, marker + ".pdf");
        await File.WriteAllBytesAsync(file, bytes);
        await File.WriteAllTextAsync(Path.Combine(runDirectory, "upload-fixture.json"), JsonSerializer.Serialize(new {
            path=file, marker, pageCount=2, sha256=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            purpose="Generated test document only. Use browser_file_upload through a visible upload UI; do not upload other local files."
        }));
        return directory;
    }
}
