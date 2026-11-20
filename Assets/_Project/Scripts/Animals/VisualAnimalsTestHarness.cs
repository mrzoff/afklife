using AfkIsland.Save;
using UnityEngine;

namespace AfkIsland.Animals
{
    /// <summary>
    /// Spawns the three catalog animals for visual QA without offline/world simulation.
    /// </summary>
    public sealed class VisualAnimalsTestHarness : MonoBehaviour
    {
        [SerializeField] private AnimalData butterfly;
        [SerializeField] private AnimalData bird;
        [SerializeField] private AnimalData frog;
        [SerializeField] private Transform animalsRoot;

        private void Start()
        {
            if (animalsRoot == null)
            {
                var go = new GameObject("Animals");
                go.transform.SetParent(transform, false);
                animalsRoot = go.transform;
            }

            if (butterfly == null)
            {
                butterfly = Resources.Load<AnimalData>("Animals/AnimalData_butterfly");
            }

            if (bird == null)
            {
                bird = Resources.Load<AnimalData>("Animals/AnimalData_bird");
            }

            if (frog == null)
            {
                frog = Resources.Load<AnimalData>("Animals/AnimalData_frog");
            }

            Spawn(butterfly, new Vector3(-1.4f, 0.35f, 0.2f), "test_butterfly");
            Spawn(bird, new Vector3(0.2f, 0.55f, 0.6f), "test_bird");
            Spawn(frog, new Vector3(1.3f, 0.05f, -0.4f), "test_frog");
        }

        private void Spawn(AnimalData def, Vector3 pos, string instanceId)
        {
            if (def == null)
            {
                Debug.LogWarning("[AFK Island] VisualAnimalsTest: missing AnimalData.");
                return;
            }

            GameObject go;
            if (def.prefab != null)
            {
                go = Instantiate(def.prefab, animalsRoot);
                go.transform.localScale = def.visualScale;
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.transform.SetParent(animalsRoot, false);
                go.transform.localScale = def.visualScale;
                Object.Destroy(go.GetComponent<Collider>());
                Debug.LogWarning($"[AFK Island] VisualAnimalsTest: prefab null for {def.animalId}");
            }

            go.name = $"Animal_{def.animalId}_{instanceId}";
            go.transform.position = pos;
            var instance = go.GetComponent<AnimalInstance>() ?? go.AddComponent<AnimalInstance>();
            instance.Configure(
                new AnimalSaveData
                {
                    instanceId = instanceId,
                    animalId = def.animalId,
                    posX = pos.x,
                    posY = pos.y,
                    posZ = pos.z,
                    state = 0,
                    spawnedUtc = 0
                },
                def);
        }
    }
}
