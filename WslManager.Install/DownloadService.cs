using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WslManager.Helpers;

namespace WslManager.Install;

public class SpeedResult
{
	public bool Ok { get; set; }

	public double Kbps { get; set; }

	public int StatusCode { get; set; }

	public bool UsedFallback { get; set; }

	public string Error { get; set; } = "";

	public string SpeedText => Ok
		? (Kbps >= 1024 ? $"{Kbps / 1024:F1} MB/s" : $"{Kbps:F0} KB/s")
		: "不可用";
}

public class DownloadProgress
{
	public long BytesReceived { get; set; }

	public long TotalBytes { get; set; }

	public double Percent => TotalBytes > 0 ? BytesReceived * 100.0 / TotalBytes : 0;

	public double Kbps { get; set; }

	public string SpeedText => Kbps >= 1024 ? $"{Kbps / 1024:F1} MB/s" : $"{Kbps:F0} KB/s";

	public string SizeText => TotalBytes > 0
		? $"{BytesReceived / 1048576.0:F1} / {TotalBytes / 1048576.0:F1} MB"
		: $"{BytesReceived / 1048576.0:F1} MB";
}

public static class DownloadService
{
	public static string DownloadDir => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"WslManager", "downloads");

	private const int SpeedProbeBytes = 4 * 1024 * 1024;
	private static readonly HttpClient Http = CreateClient();

	private static HttpClient CreateClient()
	{
		// 不设总超时：大文件在慢速国际链路上可能远超 10 分钟；
		// 改由 DownloadAsync 内的「首字节 60 秒 / 停滞 60 秒」看门狗按段兜底
		var c = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
		// 部分镜像站（aliyun/tuna/ustc）对无 User-Agent 的请求直接返回 403
		c.DefaultRequestHeaders.UserAgent.ParseAdd("WslManager/1.0");
		c.DefaultRequestHeaders.Accept.ParseAdd("*/*");
		return c;
	}


	public static string CachePathFor(string url)
	{
		var dir = DownloadDir;
		Directory.CreateDirectory(dir);
		var name = url.Split('?')[0].TrimEnd('/');
		name = name.Substring(name.LastIndexOf('/') + 1);
		if (string.IsNullOrWhiteSpace(name))
			name = "download.bin";
		return Path.Combine(dir, name);
	}

	public static async Task<SpeedResult> MeasureAsync(string url, CancellationToken ct = default)
	{
		var result = new SpeedResult();
		try
		{
			result = await ProbeRangeAsync(url, ct).ConfigureAwait(false);
			if (!result.Ok)
			{
				// 403/405 等 Range 被拒时回退为普通 GET 前几 MB 后中断
				result = await ProbePlainAsync(url, ct).ConfigureAwait(false);
				result.UsedFallback = true;
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			result = new SpeedResult { Ok = false, Error = ex.Message };
		}
		return result;
	}

	private static async Task<SpeedResult> ProbeRangeAsync(string url, CancellationToken ct)
	{
		var r = new SpeedResult();
		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, SpeedProbeBytes - 1);
		using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
		r.StatusCode = (int)resp.StatusCode;
		if (resp.StatusCode != HttpStatusCode.PartialContent && (int)resp.StatusCode != 200)
		{
			r.Error = $"HTTP {(int)resp.StatusCode}（不支持 Range）";
			return r;
		}
		var s = await ReadSpeedAsync(resp, ct).ConfigureAwait(false);
		s.StatusCode = r.StatusCode;
		return s;
	}

	private static async Task<SpeedResult> ProbePlainAsync(string url, CancellationToken ct)
	{
		var r = new SpeedResult();
		using var req = new HttpRequestMessage(HttpMethod.Get, url);
		using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
		r.StatusCode = (int)resp.StatusCode;
		if (!resp.IsSuccessStatusCode)
		{
			r.Error = $"HTTP {(int)resp.StatusCode}";
			return r;
		}
		var s = await ReadSpeedAsync(resp, ct).ConfigureAwait(false);
		s.StatusCode = r.StatusCode;
		return s;
	}

	private static async Task<SpeedResult> ReadSpeedAsync(HttpResponseMessage resp, CancellationToken ct)
	{
		var r = new SpeedResult();
		var sw = Stopwatch.StartNew();
		var buf = new byte[64 * 1024];
		long total = 0;
		using (var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
		{
			while (total < SpeedProbeBytes && sw.Elapsed.TotalSeconds < 8)
			{
				int n = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
				if (n <= 0)
					break;
				total += n;
			}
		}
		sw.Stop();
		var secs = sw.Elapsed.TotalSeconds;
		if (secs < 0.01)
			secs = 0.01;
		r.Kbps = total > 0 ? total / 1024.0 / secs : 0;
		r.Ok = total > 0;
		if (!r.Ok)
			r.Error = "无数据返回";
		return r;
	}

	public static async Task<string> DownloadAsync(
		string url,
		string destPath,
		IProgress<DownloadProgress> progress = null,
		CancellationToken ct = default)
	{
		var dir = Path.GetDirectoryName(destPath);
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);

		var tmp = destPath + ".part";
		long received = 0;
		try
		{
		var buf = new byte[128 * 1024];
		var sw = Stopwatch.StartNew();
		long lastBytes = 0;
		double kbps = 0;

		using (var req = new HttpRequestMessage(HttpMethod.Get, url))
		{
			HttpResponseMessage resp;
			try
			{
				using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
				headCts.CancelAfter(TimeSpan.FromSeconds(60));
				resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headCts.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				throw new IOException("连接下载源超时（60 秒无响应）：" + url);
			}
			using (resp)
			{
				if (!resp.IsSuccessStatusCode)
					throw new HttpRequestException($"下载失败 HTTP {(int)resp.StatusCode}：{url}");

				long total = resp.Content.Headers.ContentLength ?? 0;
				using (var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
				using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
				{
					int n;
					while (true)
					{
						try
						{
							using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
							readCts.CancelAfter(TimeSpan.FromSeconds(60));
							n = await stream.ReadAsync(buf, 0, buf.Length, readCts.Token).ConfigureAwait(false);
						}
						catch (OperationCanceledException) when (!ct.IsCancellationRequested)
						{
							throw new IOException($"下载停滞超过 60 秒（已接收 {received / 1048576.0:F1} MB，源无响应）：" + url);
						}
						if (n <= 0)
							break;
						await fs.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
						received += n;
						if (sw.ElapsedMilliseconds >= 500)
						{
							kbps = (received - lastBytes) * 1000.0 / 1024.0 / sw.ElapsedMilliseconds;
							lastBytes = received;
							sw.Restart();
							progress?.Report(new DownloadProgress
							{
								BytesReceived = received,
								TotalBytes = total,
								Kbps = kbps
							});
						}
					}
					progress?.Report(new DownloadProgress
					{
						BytesReceived = received,
						TotalBytes = total > 0 ? total : received,
						Kbps = kbps
					});
				}
			}
		}

		if (File.Exists(destPath))
			File.Delete(destPath);
		File.Move(tmp, destPath);
		}
		catch (Exception)
		{
			try
			{
				if (File.Exists(tmp))
					File.Delete(tmp);
			}
			catch
			{
			}
			throw;
		}
		SimpleLog.Write($"下载完成：{Path.GetFileName(destPath)} {received / 1048576.0:F1} MB");
		return destPath;
	}

	public static async Task<string> DownloadCachedAsync(
		string url,
		IProgress<DownloadProgress> progress = null,
		CancellationToken ct = default)
	{
		var path = CachePathFor(url);
		if (File.Exists(path))
		{
			var len = new FileInfo(path).Length;
			if (len > 0)
			{
				SimpleLog.Write($"命中缓存：{Path.GetFileName(path)} {len / 1048576.0:F1} MB");
				progress?.Report(new DownloadProgress { BytesReceived = len, TotalBytes = len, Kbps = 0 });
				return path;
			}
		}
		return await DownloadAsync(url, path, progress, ct).ConfigureAwait(false);
	}
}
