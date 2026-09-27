using System;
using System.Collections.Generic;
using System.Linq;

namespace WslManager.Install;

public enum InstallMethod
{
	WslFile,
	TarImport
}

public class MirrorSource
{
	public string Name { get; set; } = "";

	public string Url { get; set; } = "";

	public string Location { get; set; } = "";

	public bool IsOfficial { get; set; }
}

public class DistroPackage
{
	public string Key { get; set; } = "";

	public string Name { get; set; } = "";

	public string Version { get; set; } = "";

	public string OnlineName { get; set; } = "";

	public long SizeBytes { get; set; }


	public InstallMethod Method { get; set; }

	public List<MirrorSource> Sources { get; set; } = new List<MirrorSource>();

	public string SizeText => SizeBytes <= 0 ? "未知" : $"{SizeBytes / (1024.0 * 1024.0):F0} MB";

	public string MethodText => Method == InstallMethod.WslFile ? "文件安装" : "压缩包导入";
}

public static class MirrorCatalog
{
	public static readonly List<DistroPackage> Packages = new List<DistroPackage>
	{
		new DistroPackage
		{
			Key = "ubuntu2404",
			Name = "Ubuntu",
			Version = "24.04.5 LTS (Noble)",
			OnlineName = "Ubuntu-24.04",
			SizeBytes = 371L * 1024 * 1024,
			Method = InstallMethod.WslFile,
			Sources = new List<MirrorSource>
			{
				new MirrorSource { Name = "清华大学 TUNA", Location = "北京", Url = "https://mirrors.tuna.tsinghua.edu.cn/ubuntu-releases/noble/ubuntu-24.04.5-wsl-amd64.wsl" },
				new MirrorSource { Name = "阿里云", Location = "杭州", Url = "https://mirrors.aliyun.com/ubuntu-releases/noble/ubuntu-24.04.5-wsl-amd64.wsl" },
				new MirrorSource { Name = "中国科学技术大学 USTC", Location = "合肥", Url = "https://mirrors.ustc.edu.cn/ubuntu-releases/noble/ubuntu-24.04.5-wsl-amd64.wsl" },
				new MirrorSource { Name = "华为云", Location = "贵阳", Url = "https://mirrors.huaweicloud.com/ubuntu-releases/noble/ubuntu-24.04.5-wsl-amd64.wsl" },
				new MirrorSource { Name = "中国科学技术大学 USTC（cloud-images）", Location = "合肥", Url = "https://mirrors.ustc.edu.cn/ubuntu-cloud-images/wsl/releases/24.04/current/ubuntu-noble-wsl-amd64-wsl.rootfs.tar.gz" },
				new MirrorSource { Name = "Ubuntu 官方 (cloud-images)", Location = "海外", Url = "https://cloud-images.ubuntu.com/wsl/releases/24.04/current/ubuntu-noble-wsl-amd64-wsl.rootfs.tar.gz", IsOfficial = true }
			}
		},
		new DistroPackage
		{
			Key = "ubuntu2204",
			Name = "Ubuntu",
			Version = "22.04.5 LTS (Jammy)",
			OnlineName = "Ubuntu-22.04",
			SizeBytes = 344L * 1024 * 1024,
			Method = InstallMethod.WslFile,
			Sources = new List<MirrorSource>
			{
				new MirrorSource { Name = "清华大学 TUNA", Location = "北京", Url = "https://mirrors.tuna.tsinghua.edu.cn/ubuntu-releases/jammy/ubuntu-22.04.5-wsl-amd64.wsl" },
				new MirrorSource { Name = "阿里云", Location = "杭州", Url = "https://mirrors.aliyun.com/ubuntu-releases/jammy/ubuntu-22.04.5-wsl-amd64.wsl" },
				new MirrorSource { Name = "中国科学技术大学 USTC", Location = "合肥", Url = "https://mirrors.ustc.edu.cn/ubuntu-releases/jammy/ubuntu-22.04.5-wsl-amd64.wsl" },
				new MirrorSource { Name = "华为云", Location = "贵阳", Url = "https://mirrors.huaweicloud.com/ubuntu-releases/jammy/ubuntu-22.04.5-wsl-amd64.wsl" },
				new MirrorSource { Name = "中国科学技术大学 USTC（cloud-images）", Location = "合肥", Url = "https://mirrors.ustc.edu.cn/ubuntu-cloud-images/wsl/releases/22.04/current/ubuntu-jammy-wsl-amd64-wsl.rootfs.tar.gz" },
				new MirrorSource { Name = "Ubuntu 官方 (cloud-images)", Location = "海外", Url = "https://cloud-images.ubuntu.com/wsl/releases/22.04/current/ubuntu-jammy-wsl-amd64-wsl.rootfs.tar.gz", IsOfficial = true }
			}
		},
		new DistroPackage
		{
			Key = "ubuntu2604",
			Name = "Ubuntu",
			Version = "26.04.1 LTS (Resolute)",
			OnlineName = "Ubuntu-26.04",
			SizeBytes = 399L * 1024 * 1024,
			Method = InstallMethod.WslFile,
			Sources = new List<MirrorSource>
			{
				new MirrorSource { Name = "清华大学 TUNA", Location = "北京", Url = "https://mirrors.tuna.tsinghua.edu.cn/ubuntu-releases/resolute/ubuntu-26.04.1-wsl-amd64.wsl" },
				new MirrorSource { Name = "阿里云", Location = "杭州", Url = "https://mirrors.aliyun.com/ubuntu-releases/resolute/ubuntu-26.04.1-wsl-amd64.wsl" },
				new MirrorSource { Name = "中国科学技术大学 USTC", Location = "合肥", Url = "https://mirrors.ustc.edu.cn/ubuntu-releases/resolute/ubuntu-26.04.1-wsl-amd64.wsl" },
				new MirrorSource { Name = "华为云", Location = "贵阳", Url = "https://mirrors.huaweicloud.com/ubuntu-releases/resolute/ubuntu-26.04.1-wsl-amd64.wsl" }
			}
		},
		new DistroPackage
		{
			Key = "ubuntualias",
			Name = "Ubuntu",
			Version = "",
			OnlineName = "Ubuntu",
			SizeBytes = 0,
			Method = InstallMethod.WslFile,
			Sources = new List<MirrorSource>()
		},
		new DistroPackage
		{
			Key = "alpine3226",
			Name = "Alpine",
			Version = "3.22.6 (minirootfs)",
			OnlineName = "",
			SizeBytes = 4L * 1024 * 1024,
			Method = InstallMethod.TarImport,
			Sources = new List<MirrorSource>
			{
				new MirrorSource { Name = "Alpine 官方 CDN", Location = "海外", Url = "https://dl-cdn.alpinelinux.org/alpine/v3.22/releases/x86_64/alpine-minirootfs-3.22.6-x86_64.tar.gz", IsOfficial = true },
				new MirrorSource { Name = "阿里云", Location = "杭州", Url = "https://mirrors.aliyun.com/alpine/v3.22/releases/x86_64/alpine-minirootfs-3.22.6-x86_64.tar.gz" },
				new MirrorSource { Name = "清华大学 TUNA", Location = "北京", Url = "https://mirrors.tuna.tsinghua.edu.cn/alpine/v3.22/releases/x86_64/alpine-minirootfs-3.22.6-x86_64.tar.gz" },
				new MirrorSource { Name = "中国科学技术大学 USTC", Location = "合肥", Url = "https://mirrors.ustc.edu.cn/alpine/v3.22/releases/x86_64/alpine-minirootfs-3.22.6-x86_64.tar.gz" },
				new MirrorSource { Name = "华为云", Location = "贵阳", Url = "https://mirrors.huaweicloud.com/alpine/v3.22/releases/x86_64/alpine-minirootfs-3.22.6-x86_64.tar.gz" }
			}
		},
		new DistroPackage
		{
			Key = "debian12",
			Name = "Debian",
			Version = "12 Bookworm",
			OnlineName = "Debian",
			SizeBytes = 0,
			Method = InstallMethod.WslFile,
			Sources = new List<MirrorSource>()
		},
		new DistroPackage
		{
			Key = "kalilinux",
			Name = "Kali Linux",
			Version = "Rolling",
			OnlineName = "kali-linux",
			SizeBytes = 0,
			Method = InstallMethod.WslFile,
			Sources = new List<MirrorSource>()
		}
	};

	public static IEnumerable<DistroPackage> WithOfficialFirst()
	{
		return Packages.OrderByDescending(p => p.Sources.Any(s => !s.IsOfficial));
	}
}
