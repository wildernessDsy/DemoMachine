using UnityEngine;

/// <summary>
/// WebGL 里没有系统字体，Font.CreateDynamicFontFromOSFont 会返回空，中文全部不显示�?/// 统一从这里取字体：优先用工程内置�?Resources/Fonts/simhei.ttf，取不到再退回系统字体（编辑器里）�?/// </summary>
public static class CjkFont
{
    static Font _font;
    static bool _tried;

    public static Font Get()
    {
        if (_tried)
            return _font;

        _tried = true;

        // 打包�?Resources 的中文字体，WebGL 下也能用
        _font = Resources.Load<Font>("Fonts/simhei");

        if (_font == null)
        {
            try
            {
                _font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei" }, 40);
            }
            catch
            {
                _font = null;
            }
        }

        return _font;
    }
}
