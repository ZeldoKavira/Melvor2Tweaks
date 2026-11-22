using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;

namespace Melvor2Tweaks.Remote;

/// <summary>
/// Game textures for the phone app: <see cref="Id"/> registers a texture (an item, skill, recipe... icon) and
/// returns a short id for /icon/&lt;id&gt;; the PNG is rendered once, at most 64px, on the main thread.
/// </summary>
internal static class Icons
{
    private static readonly ConcurrentDictionary<string, Texture2D> s_textures = new();
    private static readonly ConcurrentDictionary<string, byte[]> s_png = new();

    /// <summary>Icon id for a texture, or for the first Icon / Media texture property of a game object.</summary>
    internal static string? Id(object? source)
    {
        var texture = source as Texture2D ?? Find(source);
        if (texture == null) return null;
        string key = texture.ResourcePath is { Length: > 0 } path
            ? ((uint)path.GetHashCode()).ToString("x8") + (uint)path.Length
            : "i" + texture.GetInstanceId();
        s_textures.TryAdd(key, texture);
        return key;
    }

    private static Texture2D? Find(object? source)
    {
        if (source == null) return null;
        var t = Traverse.Create(source);
        foreach (string name in new[] { "Icon", "Media", "Sprite", "Image", "Texture" })
            if (t.Property(name).PropertyExists() && t.Property(name).GetValue() is Texture2D tex) return tex;
        return null;
    }

    internal static async Task<Response> Serve(string file)
    {
        string id = file.EndsWith(".png") ? file[..^4] : file;
        if (!s_png.TryGetValue(id, out var png))
        {
            if (!s_textures.TryGetValue(id, out var texture)) return Response.Error("unknown icon", 404);
            png = await RemoteServer.OnMain(() => Render(texture));
            if (png.Length > 0) s_png[id] = png;
        }
        var r = new Response { ContentType = "image/png", Body = png };
        r.ExtraHeaders.Add("Cache-Control: private, max-age=86400");
        return r;
    }

    private static byte[] Render(Texture2D texture)
    {
        try
        {
            var image = texture.GetImage();
            if (image == null) return Array.Empty<byte>();
            if (image.IsCompressed()) image.Decompress();
            int w = image.GetWidth(), h = image.GetHeight();
            if (w > 64 || h > 64)
            {
                float scale = 64f / Math.Max(w, h);
                image.Resize(Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)), Image.Interpolation.Lanczos);
            }
            return image.SavePngToBuffer();
        }
        catch (Exception e)
        {
            Plugin.Log("Phone access: icon render failed: " + e.Message);
            return Array.Empty<byte>();
        }
    }
}
