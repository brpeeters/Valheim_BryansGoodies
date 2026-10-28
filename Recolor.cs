using System.Collections.Generic;
using UnityEngine;

namespace BryansGoodies
{
    // "Colorize" recoloring: each pixel takes the hue and saturation of the target color but
    // keeps its own brightness (relief / details remain), instead of a simple multiply.
    // The game's textures are not CPU-readable: they are copied through a RenderTexture.
    public static class Recolor
    {
        static readonly Dictionary<(int, int), Sprite> sprites = new Dictionary<(int, int), Sprite>();
        static readonly Dictionary<(int, int, int), Texture2D> textures = new Dictionary<(int, int, int), Texture2D>();

        public static int Key(Color c) => ((int)(c.r * 255) << 16) | ((int)(c.g * 255) << 8) | (int)(c.b * 255);

        // Applies the hue/saturation of 'target' to a pixel. strength 0 = original, 1 = fully recolored.
        public static Color32 Colorize(Color32 src, float th, float ts, float tv, float strength)
        {
            Color.RGBToHSV(src, out _, out _, out float v);
            Color c = Color.HSVToRGB(th, ts, v * tv);
            if (strength < 1f) c = Color.Lerp(src, c, strength);
            return new Color32((byte)(c.r * 255f + 0.5f), (byte)(c.g * 255f + 0.5f), (byte)(c.b * 255f + 0.5f), src.a);
        }

        // Recolored icon (cached per sprite + color).
        public static Sprite Sprite(Sprite src, Color target)
        {
            if (src == null) return null;
            var key = (src.GetInstanceID(), Key(target));
            if (sprites.TryGetValue(key, out var cached) && cached != null) return cached;

            Rect r = src.textureRect;
            var tex = ReadPixels(src.texture, new RectInt((int)r.x, (int)r.y, (int)r.width, (int)r.height), true);
            if (tex == null) return src;
            Process(tex, target, 1f);
            tex.Apply(true, true);
            var sp = UnityEngine.Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(src.pivot.x / r.width, src.pivot.y / r.height), src.pixelsPerUnit);
            sprites[key] = sp;
            return sp;
        }

        // Whole recolored texture (cached per texture + color + strength).
        public static Texture2D Texture(Texture src, Color target, float strength, out (int, int, int) key)
        {
            key = (src.GetInstanceID(), Key(target), Mathf.RoundToInt(strength * 20f));
            if (textures.TryGetValue(key, out var cached) && cached != null) return cached;
            var tex = ReadPixels(src, new RectInt(0, 0, src.width, src.height), true);
            if (tex == null) return null;
            Process(tex, target, strength);
            tex.Apply(true, true); // mipmaps + no longer readable: frees the CPU copy
            tex.name = src.name + "_bg" + key.Item2.ToString("X6");
            tex.wrapMode = src.wrapMode;
            tex.filterMode = src.filterMode;
            tex.anisoLevel = src.anisoLevel;
            textures[key] = tex;
            return tex;
        }

        public static void ReleaseTexturesExcept(HashSet<(int, int, int)> used)
        {
            var drop = new List<(int, int, int)>();
            foreach (var kv in textures)
                if (!used.Contains(kv.Key)) drop.Add(kv.Key);
            foreach (var k in drop)
            {
                if (textures[k] != null) Object.Destroy(textures[k]);
                textures.Remove(k);
            }
        }

        static void Process(Texture2D tex, Color target, float strength)
        {
            Color.RGBToHSV(target, out float th, out float ts, out float tv);
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++) px[i] = Colorize(px[i], th, ts, tv, strength);
            tex.SetPixels32(px);
        }

        static Texture2D ReadPixels(Texture src, RectInt region, bool mipmaps)
        {
            if (src == null || region.width <= 0 || region.height <= 0) return null;
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(region.width, region.height, TextureFormat.RGBA32, mipmaps, false);
                tex.ReadPixels(new Rect(region.x, region.y, region.width, region.height), 0, 0, false);
                return tex;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
