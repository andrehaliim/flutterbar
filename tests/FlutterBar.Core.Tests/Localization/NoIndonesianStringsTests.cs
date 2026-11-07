using System.Text;
using System.Text.RegularExpressions;

namespace FlutterBar.Core.Tests.Localization;

/// <summary>
/// Guards the "all user-facing text is English" rule: scans the app sources (comments excluded) for
/// common Indonesian words used as whole words.
/// </summary>
public class NoIndonesianStringsTests
{
    private static readonly string[] Words =
    [
        // required minimum
        "tidak", "belum", "sedang", "dibatalkan", "berjalan", "pilih", "gagal", "baris", "lanjutan", "buka",
        "keluar", "kelola", "tambah", "cari", "hanya", "bersihkan", "salin", "ringkas", "lengkap", "batal",
        "ganti", "sesi", "sembunyi", "perubahan", "ditemukan",
        // extras found in the inventory
        "dengan", "yang", "dari", "untuk", "atau", "sudah", "bisa", "dulu", "terbaca", "tersedia", "dihentikan",
        "menjalankan", "memuat", "ada", "dan", "terhubung", "disalin", "disimpan", "diubah", "dipakai", "dikenal",
        "lagi", "saja", "semua", "jalankan", "menyimpan", "mencari", "dicentang", "diimport", "menampilkan",
        "akan", "setelah", "kosong", "bukan", "tampil", "tampilkan", "kosongkan", "matikan", "nyalakan",
        "terdaftar", "perbaiki", "rusak", "dicadangkan", "diganti", "gunakan", "hilang", "memakai", "mengirim",
        "menjawab", "detik", "jawaban", "nilai", "perintah", "argumen", "tombol", "catatan", "terpilih",
        "aplikasi", "disiapkan", "membaca", "berhenti", "menyalakan", "terlebih", "masih", "tersembunyi",
        "menit", "kutip", "jangan", "mohon", "silakan", "berhasil", "ditambahkan", "dihapus",
        "menghapus", "dibuka", "membuka", "menyalin", "kembali", "simpan", "hapus", "lihat", "pilihan",
        "daftar", "tutup", "baca", "pindah", "harus", "berupa", "menunggu", "kesalahan", "menyiapkan",
        "berikutnya", "sebelumnya", "selesai", "siap", "membutuhkan", "angka", "notifikasi", "absolut",
        "nyalakan", "menyalakan", "mengalami", "tambahkan", "wajib", "diisi", "diimpor", "ditolak", "bernilai", "berisi", "stabilitas", "terbaca", "dihentikan", "tepat", "kiri", "kanan", "boleh", "kedalaman", "ditelusuri",
    ];

    private static readonly Regex WordRegex = new(
        @"\b(" + string.Join('|', Words) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Sources_ContainNoIndonesianWords()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        var hits = new List<string>();

        var files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsBuildOutput(f, src));

        foreach (var file in files)
        {
            var isXaml = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            var lines = File.ReadAllLines(file);
            var inBlockComment = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripComments(lines[i], isXaml, ref inBlockComment);
                var m = WordRegex.Match(code);
                if (m.Success)
                {
                    hits.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: '{m.Value}' in: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(hits.Count == 0, "Indonesian words found in source (user-facing text must be English):\n" + string.Join('\n', hits));
    }

    private static bool IsBuildOutput(string file, string src)
    {
        var parts = Path.GetRelativePath(src, file).Split(Path.DirectorySeparatorChar);
        return parts.Any(p => p.Equals("bin", StringComparison.OrdinalIgnoreCase) || p.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Removes comments from one line, honouring double-quoted strings; tracks block comments across lines.</summary>
    private static string StripComments(string line, bool xaml, ref bool inBlock)
    {
        var sb = new StringBuilder();
        var inString = false;
        var i = 0;
        while (i < line.Length)
        {
            if (inBlock)
            {
                var close = xaml ? "-->" : "*/";
                var end = line.IndexOf(close, i, StringComparison.Ordinal);
                if (end < 0)
                {
                    return sb.ToString();
                }

                inBlock = false;
                i = end + close.Length;
                continue;
            }

            if (xaml)
            {
                if (string.CompareOrdinal(line, i, "<!--", 0, 4) == 0)
                {
                    inBlock = true;
                    i += 4;
                    continue;
                }

                sb.Append(line[i++]);
                continue;
            }

            var c = line[i];
            if (inString)
            {
                sb.Append(c);
                if (c == (char)92 && i + 1 < line.Length)
                {
                    sb.Append(line[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                i++;
                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return sb.ToString();
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                inBlock = true;
                i += 2;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FlutterBar.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("FlutterBar.sln not found above " + AppContext.BaseDirectory);
    }
}
