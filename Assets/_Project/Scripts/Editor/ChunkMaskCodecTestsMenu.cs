#if UNITY_EDITOR
using AfkIsland.Save;
using UnityEditor;
using UnityEngine;

namespace AfkIsland.EditorTools
{
    public static class ChunkMaskCodecTestsMenu
    {
        [MenuItem("AFK Island/Tests/ChunkMaskCodec RoundTrip")]
        public static void RoundTrip()
        {
            var stages = new byte[ChunkMaskCodec.CellCount];
            for (var i = 0; i < stages.Length; i++)
            {
                stages[i] = (byte)(i % 5);
            }

            var b64 = ChunkMaskCodec.EncodeToBase64(stages);
            if (!ChunkMaskCodec.TryDecodeBase64(b64, out var decoded, out var error))
            {
                Debug.LogError("[AFK Island] Mask decode failed: " + error);
                return;
            }

            for (var i = 0; i < stages.Length; i++)
            {
                if (stages[i] != decoded[i])
                {
                    Debug.LogError($"[AFK Island] Mask mismatch at {i}: {stages[i]} != {decoded[i]}");
                    return;
                }
            }

            var mature = ChunkMaskCodec.CreateMatureMaskBase64();
            if (!ChunkMaskCodec.TryDecodeBase64(mature, out var matureStages, out error))
            {
                Debug.LogError("[AFK Island] Mature mask failed: " + error);
                return;
            }

            Debug.Log($"[AFK Island] ChunkMaskCodec OK. Base64 length={b64.Length}, mature cells={matureStages.Length}");
        }
    }
}
#endif
