using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// Solid hit mesh for grown land so taps stop on the island instead of the ocean plane underneath.
    /// </summary>
    public static class LandHitCollider
    {
        public static void Apply(GameObject root, Mesh mesh)
        {
            if (root == null)
            {
                return;
            }

            var col = root.GetComponent<MeshCollider>();
            if (mesh == null || mesh.vertexCount < 3)
            {
                if (col != null)
                {
                    col.sharedMesh = null;
                    col.enabled = false;
                }

                return;
            }

            if (col == null)
            {
                col = root.AddComponent<MeshCollider>();
            }

            col.convex = false;
            col.isTrigger = false;
            col.enabled = true;
            if (col.sharedMesh == mesh)
            {
                return;
            }

            // Reassigning the same slot does not recook. Clear first when the chunk mesh is rebuilt.
            col.sharedMesh = null;
            col.sharedMesh = mesh;
        }
    }
}
