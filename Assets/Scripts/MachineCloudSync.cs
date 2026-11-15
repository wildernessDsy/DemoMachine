using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 可选的云端通道：把当日存档 POST 到一个小接口上，让“静态托管的 WebGL 站点”也能变成动态网站。
///
/// 三种用法：
/// 1) 完全不配置 → 纯本地模式（IndexedDB / PlayerPrefs），静态托管也能保存，刷新不丢；
/// 2) 页面地址带参数 → https://你的站点/?api=https://你的接口域名，打开就自动配置；
/// 3) 代码里调 MachineDataRecorder.SetCloudEndpoint("https://...") 或写 PlayerPrefs。
///
/// 接口约定（仓库里 Tools/DynamicSite/server 有可直接运行的实现）：
///   POST {baseUrl}/api/day     Body: MachineSavedDay 的 JSON     → 201 / 200
///   GET  {baseUrl}/api/day?date=yyyy-MM-dd&machine=0           → MachineSavedDay
///   GET  {baseUrl}/api/days                                    → 日期列表 + 概况
///
/// WebGL 下是浏览器直接发请求，接口必须允许跨域（Access-Control-Allow-Origin）。
/// </summary>
public static class MachineCloudSync
{
    public const string BaseUrlPrefsKey = "dsy.cloud.baseUrl";
    public const string ApiPath = "/api/day";

    static bool _initialized;

    /// <summary>接口根地址，例如 https://api.example.com；空字符串表示纯本地模式。</summary>
    public static string BaseUrl { get; private set; } = string.Empty;

    public static bool Enabled => !string.IsNullOrEmpty(BaseUrl);

    /// <summary>最近一次同步的状态文案，面板直接显示。</summary>
    public static string Status { get; private set; } = "本地模式";

    public static void Initialize()
    {
        if (_initialized)
            return;

        _initialized = true;
        Reload();
    }

    public static void Reload()
    {
        string url = PlayerPrefs.GetString(BaseUrlPrefsKey, string.Empty);
        if (string.IsNullOrEmpty(url))
            url = ReadFromPageUrl();

        BaseUrl = Normalize(url);
        Status = Enabled ? "云端已配置" : "本地模式";
    }

    /// <summary>运行时设置接口地址（传空字符串即关闭云端）。</summary>
    public static void SetBaseUrl(string url)
    {
        string normalized = Normalize(url);

        if (string.IsNullOrEmpty(normalized))
        {
            PlayerPrefs.DeleteKey(BaseUrlPrefsKey);
        }
        else
        {
            PlayerPrefs.SetString(BaseUrlPrefsKey, normalized);
        }

        PlayerPrefs.Save();

        BaseUrl = normalized;
        Status = Enabled ? "云端已配置" : "本地模式";
        Debug.Log($"[MachineCloudSync] 接口地址 = {(Enabled ? BaseUrl : "（未配置，本地模式）")}");
    }

    static string Normalize(string url)
    {
        if (string.IsNullOrEmpty(url))
            return string.Empty;

        url = url.Trim().TrimEnd('/');
        if (url.Length == 0)
            return string.Empty;

        // 只写域名时补协议，避免 WebGL 里 UnityWebRequest 报 "Invalid URL"。
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url.TrimStart('/');
        }

        return url;
    }

    /// <summary>从页面地址的 ?api= 参数里读接口地址（WebGL 打包后最省事的一种配置方式）。</summary>
    static string ReadFromPageUrl()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        try
        {
            string page = Application.absoluteURL;
            if (string.IsNullOrEmpty(page))
                return string.Empty;

            int queryIndex = page.IndexOf('?');
            if (queryIndex < 0 || queryIndex + 1 >= page.Length)
                return string.Empty;

            string query = page.Substring(queryIndex + 1);
            int hashIndex = query.IndexOf('#');
            if (hashIndex >= 0)
                query = query.Substring(0, hashIndex);

            string[] pairs = query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                string pair = pairs[i];
                int eq = pair.IndexOf('=');
                if (eq <= 0)
                    continue;

                if (pair.Substring(0, eq) != "api")
                    continue;

                return Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MachineCloudSync] 解析页面参数失败：{e.Message}");
        }
#endif
        return string.Empty;
    }

    /// <summary>把一台机器一天的存档推到云端。</summary>
    public static void Push(MonoBehaviour host, MachineSavedDay day, Action<bool, string> done)
    {
        if (!Enabled)
        {
            Status = "本地模式";
            if (done != null)
                done(false, "未配置云端接口");
            return;
        }

        if (host == null || day == null)
        {
            if (done != null)
                done(false, "没有可上传的数据");
            return;
        }

        host.StartCoroutine(PushRoutine(day, done));
    }

    static IEnumerator PushRoutine(MachineSavedDay day, Action<bool, string> done)
    {
        string url = BaseUrl + ApiPath;
        string payload = JsonUtility.ToJson(day);
        byte[] body = Encoding.UTF8.GetBytes(payload);

        Status = "正在上传…";

        UnityWebRequest request = null;
        try
        {
            request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 15,
            };
            request.SetRequestHeader("Content-Type", "application/json;charset=UTF-8");
        }
        catch (Exception e)
        {
            Status = "上传失败：" + e.Message;
            if (request != null)
                request.Dispose();
            if (done != null)
                done(false, Status);
            yield break;
        }

        yield return request.SendWebRequest();

        bool ok = request.result == UnityWebRequest.Result.Success;
        string message;

        if (ok)
        {
            message = "云端已同步 " + DateTime.Now.ToString("HH:mm:ss");
            day.uploaded = true;
            day.uploadedAt = MachineDayStore.NowStamp();
            MachineDayStore.Save(day);
        }
        else
        {
            message = DescribeFailure(request);
        }

        Status = message;

        if (request != null)
            request.Dispose();

        Debug.Log("[MachineCloudSync] " + message);

        if (done != null)
            done(ok, message);
    }

    static string DescribeFailure(UnityWebRequest request)
    {
        if (request == null)
            return "上传失败";

        string detail = request.error;
        if (string.IsNullOrEmpty(detail))
            detail = "HTTP " + request.responseCode;

        // 顺手把服务端的错误信息带出来，方便排查跨域 / 路径写错。
        string bodyText = null;
        if (request.downloadHandler != null)
            bodyText = request.downloadHandler.text;

        if (!string.IsNullOrEmpty(bodyText))
        {
            if (bodyText.Length > 120)
                bodyText = bodyText.Substring(0, 120) + "…";
            detail = detail + " · " + bodyText;
        }

        return "上传失败：" + detail;
    }

    /// <summary>拉取某天某台机器的云端存档（供查看页 / 二次打开时对比用）。</summary>
    public static IEnumerator Pull(string date, int machineIndex, Action<MachineSavedDay> done)
    {
        if (!Enabled)
        {
            if (done != null)
                done(null);
            yield break;
        }

        string url = BaseUrl + ApiPath +
                     "?date=" + UnityWebRequest.EscapeURL(date) +
                     "&machine=" + machineIndex;

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = 15;
            yield return request.SendWebRequest();

            MachineSavedDay day = null;
            if (request.result == UnityWebRequest.Result.Success && request.downloadHandler != null)
            {
                try
                {
                    day = JsonUtility.FromJson<MachineSavedDay>(request.downloadHandler.text);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[MachineCloudSync] 解析云端数据失败：{e.Message}");
                }
            }

            if (done != null)
                done(day);
        }
    }
}
