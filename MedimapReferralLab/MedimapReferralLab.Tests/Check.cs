namespace MedimapReferralLab.Tests;

/// <summary>テストフレームワークの代わりの最小限の検査。</summary>
internal sealed class Check
{
    private int _passed;
    private readonly List<string> _failures = [];
    private string _group = "";

    public void Group(string name)
    {
        _group = name;
        Console.WriteLine($"\n■ {name}");
    }

    public void That(bool condition, string name, string? detail = null)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  ok   {name}");
        }
        else
        {
            var message = detail is null ? name : $"{name} — {detail}";
            _failures.Add($"[{_group}] {message}");
            Console.WriteLine($"  FAIL {message}");
        }
    }

    public void Equal<T>(T expected, T actual, string name) =>
        That(EqualityComparer<T>.Default.Equals(expected, actual), name, $"期待 {expected} / 実際 {actual}");

    public int Summarize()
    {
        Console.WriteLine($"\n{_passed} 件成功 / {_failures.Count} 件失敗");
        foreach (var f in _failures) Console.WriteLine($"  FAIL {f}");
        return _failures.Count == 0 ? 0 : 1;
    }
}
