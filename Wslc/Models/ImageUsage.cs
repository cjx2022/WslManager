using System.Collections.Generic;

namespace WslManager.Wslc.Models;

/// <summary>
/// 「这个镜像被哪些容器占用」。
///
/// 为什么需要它：wslc 删除**被容器引用**的镜像时不会报错 ——
/// `wslc image rm -f &lt;镜像&gt;` 实测 exit=0，输出只有「已取消标记」两行，
/// **镜像层不删除、磁盘空间不释放**，只是把 tag 摘掉，镜像从 `image ls` 里消失，
/// 看上去像删成功了。所以必须在列表里先标出来，别让用户以为空间回收了。
/// （2026-10-10 实测，WSL 3.0.1.0）
/// </summary>
public sealed class ImageUsage
{
    /// <summary>正在运行的容器名（这些容器一旦被删，镜像层才可能被回收）。</summary>
    public List<string> RunningNames { get; } = new();

    /// <summary>已停止但未删除的容器名 —— 同样按住镜像层不放。</summary>
    public List<string> StoppedNames { get; } = new();

    public int RunningCount => RunningNames.Count;
    public int StoppedCount => StoppedNames.Count;
    public int Total => RunningCount + StoppedCount;
    public bool IsInUse => Total > 0;

    public void Add(string containerName, bool running)
    {
        var n = containerName ?? "";
        if (n.Length == 0) return;
        var target = running ? RunningNames : StoppedNames;
        if (!target.Contains(n)) target.Add(n);
    }
}
