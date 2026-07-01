using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Downloader;
using Nirvana.Common.Utils.Progress;
using Serilog;

namespace Nirvana.Game.Launcher.Utils;

public static class DownloadUtil {
    private static readonly HttpClient SimpleHttpClient = new() {
        Timeout = TimeSpan.FromMinutes(5)
    };

    /**
     * 简单下载（不使用 Downloader 库），用于 UI 更新等需要避免 429 的场景
     * 遇到 429 时等待 10 秒后重试
     */
    public static async Task<bool> DownloadSimpleAsync(string url, string destinationPath, Action<double>? downloadProgress = null)
    {
        const int maxRetries = 2;
        for (var retry = 0; retry <= maxRetries; retry++) {
            try {
                using var response = await SimpleHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) {
                    var delay = (retry + 1) * 10; // 10s, 20s, 30s
                    Log.Warning("429 Too Many Requests, waiting {0}s before retry {1}/{2}...", delay, retry + 1, maxRetries);
                    await Task.Delay(delay * 1000);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[8192];
                var totalRead = 0L;
                int read;
                while ((read = await contentStream.ReadAsync(buffer)) > 0) {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    totalRead += read;
                    if (totalBytes > 0) {
                        downloadProgress?.Invoke(100.0 * totalRead / totalBytes);
                    }
                }

                return true;
            } catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests) {
                var delay = (retry + 1) * 10;
                Log.Warning("429 Too Many Requests, waiting {0}s before retry {1}/{2}...", delay, retry + 1, maxRetries);
                await Task.Delay(delay * 1000);
            } catch (TaskCanceledException) {
                Log.Information("Download canceled: {0}", url);
                throw;
            } catch (Exception ex) when (retry < maxRetries) {
                Log.Warning("Download failed (retry {0}/{1}): {2}\n{3}", retry + 1, maxRetries, url, ex.Message);
                await Task.Delay(3000);
            }
        }

        throw new HttpRequestException($"Download failed after {maxRetries} retries: {url}");
    }

    public static async Task<bool> DownloadAsync(string url, string destinationPath, Action<double>? downloadProgress = null)
    {
        try {
            if (url.Contains("netease.com")) {
                Log.Information("Downloading \"{0}\" from {1}", destinationPath, url);
            }

            var tcs = new TaskCompletionSource<bool>();

            var downloadOpt = new DownloadConfiguration {
                ChunkCount = 1, // 单块下载，避免并发请求
                MaxTryAgainOnFailure = 2, // 最多重试2次，避免429
                ParallelDownload = false, // 禁用并行下载
                EnableAutoResumeDownload = true // 启用自动续传功能
            };

            await using var downloader = new DownloadService(downloadOpt);

            var lastPercentage = -1.0;

            // 注册进度更新事件
            downloader.DownloadProgressChanged += (_, e) => {
                if (Math.Abs(e.ProgressPercentage - lastPercentage) < 0.1) {
                    return;
                }

                lastPercentage = e.ProgressPercentage;
                downloadProgress?.Invoke(e.ProgressPercentage);
            };

            downloader.DownloadFileCompleted += (_, e) => {
                if (e.Error != null) {
                    Log.Error("Download failed for {0}\n{1}", url, e.Error);
                    tcs.TrySetException(e.Error);
                } else if (e.Cancelled) {
                    Log.Information("Download canceled: {0}", url);
                    tcs.TrySetCanceled();
                } else {
                    tcs.TrySetResult(true);
                }
            };

            await downloader.DownloadFileTaskAsync(url, destinationPath);
            return await tcs.Task;
        } catch (TaskCanceledException) {
            Log.Information("Download canceled: {0}", url);
            throw;
        } catch (Exception ex) {
            Log.Error("Download failed for {0}\n{1}", url, ex);
            throw;
        }
    }

    /**
     * 更新文件
     * @param url 下载地址
     * @param path 保存路径
     * @param name 下载名称
     */
    public static async Task DownloadAsync(string url, string path, string name, SyncCallback<SyncProgressBarUtil.ProgressReport>? progress = null)
    {
        var uiProgress = SyncCallback.Create(progress);
        await DownloadAsync(url, path, dp => {
            uiProgress.Report(new SyncProgressBarUtil.ProgressReport {
                Percent = dp,
                Message = $"Downloading {name}"
            });
        });
    }
}