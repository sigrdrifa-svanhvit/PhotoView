using System.Text;
using PhotoView.Models;

namespace PhotoView.Tests;

internal static class Program
{
    private static int _failures;

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cmp = NaturalStringComparer.Instance;

        RunInvariantTests(cmp);

        if (args.Length > 0 && args[0].EndsWith(".epub", StringComparison.OrdinalIgnoreCase))
            RunEpub(args[0]);
        else if (args.Length > 0)
            RunArchiveKeys(cmp, args[0]);

        if (args.Length > 1)
            RunKeysFile(cmp, args[1]);

        Console.WriteLine(_failures == 0
            ? "ALL TESTS PASSED"
            : $"{_failures} TEST(S) FAILED");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    private static void RunInvariantTests(NaturalStringComparer cmp)
    {
        // 反対称性・自分自身・推移性をランダム文字列で検証
        var rnd = new Random(12345);
        var pool = new[]
        {
            "a", "b", "img", "photo", "page", "IMG", "Photo", "10", "2", "01", "001", "1",
            "うぽって", "コミック", "巻", "!", "(", ")", "[", "]", "20", "3", "09", "9",
            "upotte", "01", "02", "0001", "1_1", "1-1", "1.1",
        };
        var samples = new List<string>();
        for (int i = 0; i < 4000; i++)
        {
            int n = rnd.Next(1, 4);
            var sb = new StringBuilder();
            for (int j = 0; j < n; j++)
                sb.Append(pool[rnd.Next(pool.Length)]);
            samples.Add(sb.ToString());
        }

        // 自分自身
        foreach (var s in samples)
            Check(cmp.Compare(s, s) == 0, $"reflexivity failed: [{s}]");

        // 反対称性
        for (int i = 0; i < samples.Count; i++)
            for (int j = 0; j < samples.Count; j++)
            {
                int ab = cmp.Compare(samples[i], samples[j]);
                int ba = cmp.Compare(samples[j], samples[i]);
                Check(
                    ab == 0 ? ba == 0 : Math.Sign(ab) == -Math.Sign(ba),
                    $"antisym failed: [{samples[i]}] vs [{samples[j]}] ab={ab} ba={ba}");
            }

        // 推移性
        for (int i = 0; i < 300; i++)
            for (int j = 0; j < 300; j++)
                for (int k = 0; k < 300; k++)
                {
                    int ab = cmp.Compare(samples[i], samples[j]);
                    int bc = cmp.Compare(samples[j], samples[k]);
                    if (ab < 0 && bc < 0 && cmp.Compare(samples[i], samples[k]) > 0)
                        Check(false, $"transitivity failed: [{samples[i]}] [{samples[j]}] [{samples[k]}]");
                }

        // ソートが例外なく完了し、正しい順序になること
        var sorted = samples.OrderBy(s => s, cmp).ToList();
        for (int i = 0; i + 1 < sorted.Count; i++)
            Check(cmp.Compare(sorted[i], sorted[i + 1]) <= 0,
                $"sort order failed: [{sorted[i]}] > [{sorted[i + 1]}]");
    }

    private static void RunKeysFile(NaturalStringComparer cmp, string path)
    {
        try
        {
            var keys = File.ReadAllLines(path)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            Console.WriteLine($"keys file: {keys.Count}");

            for (int i = 0; i < keys.Count; i++)
            {
                Check(cmp.Compare(keys[i], keys[i]) == 0, $"self failed: [{keys[i]}]");
                for (int j = 0; j < keys.Count; j++)
                {
                    int ab = cmp.Compare(keys[i], keys[j]);
                    int ba = cmp.Compare(keys[j], keys[i]);
                    Check(
                        ab == 0 ? ba == 0 : Math.Sign(ab) == -Math.Sign(ba),
                        $"antisym failed: [{keys[i]}] vs [{keys[j]}]");
                }
            }

            var sorted = keys.OrderBy(k => k, cmp).ToList();
            for (int i = 0; i + 1 < sorted.Count; i++)
                Check(cmp.Compare(sorted[i], sorted[i + 1]) <= 0, "sort order failed");
            Console.WriteLine("keys file sort OK");
            foreach (var k in sorted)
                Console.WriteLine("  " + k);
        }
        catch (Exception ex)
        {
            Check(false, "keys file read failed: " + ex.Message);
        }
    }

    private static void RunArchiveKeys(NaturalStringComparer cmp, string path)
    {
        try
        {
            using var archive = SharpCompress.Archives.ArchiveFactory.Open(path);
            var keys = archive.Entries
                .Where(e => !e.IsDirectory)
                .Select(e => e.Key ?? "")
                .ToList();
            Console.WriteLine($"archive keys: {keys.Count}");

            for (int i = 0; i < keys.Count; i++)
            {
                Check(cmp.Compare(keys[i], keys[i]) == 0, $"self failed: [{keys[i]}]");
                for (int j = 0; j < keys.Count; j++)
                {
                    int ab = cmp.Compare(keys[i], keys[j]);
                    int ba = cmp.Compare(keys[j], keys[i]);
                    Check(
                        ab == 0 ? ba == 0 : Math.Sign(ab) == -Math.Sign(ba),
                        $"antisym failed: [{keys[i]}] vs [{keys[j]}]");
                }
            }

            var sorted = keys.OrderBy(k => k, cmp).ToList();
            for (int i = 0; i + 1 < sorted.Count; i++)
                Check(cmp.Compare(sorted[i], sorted[i + 1]) <= 0, "sort order failed");
            Console.WriteLine("archive sort OK");
        }
        catch (Exception ex)
        {
            Check(false, "archive open failed: " + ex.Message);
        }
    }

    private static void RunEpub(string path)
    {
        try
        {
            using var col = MediaCollection.Open(path, out int startIndex);
            Console.WriteLine($"epub pages: {col.Count}  startIndex: {startIndex}");
            for (int i = 0; i < col.Count; i++)
                Console.WriteLine($"  {i}: {col[i].Name}");

            Check(col.Count > 0, "epub: ページが見つかりません");
            for (int i = 0; i < col.Count; i++)
            {
                using var s = col[i].Open();
                var buf = new byte[4];
                Check(s.Read(buf, 0, buf.Length) > 0, $"epub page {i} を読み込めません: {col[i].Name}");
            }
        }
        catch (Exception ex)
        {
            Check(false, "epub open failed: " + ex.Message);
        }
    }

    private static void Check(bool ok, string message)
    {
        if (!ok)
        {
            _failures++;
            Console.WriteLine("FAIL: " + message);
        }
    }
}
