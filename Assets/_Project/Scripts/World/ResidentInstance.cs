using System.Collections.Generic;
using UnityEngine;

namespace AfkIsland.World
{
    /// <summary>
    /// One resident visual. Has no Update of its own: ResidentDirector moves it.
    /// Position is presentation only and is never written to the save.
    /// </summary>
    public sealed class ResidentInstance : MonoBehaviour
    {
        public string ResidentId { get; private set; }
        public ResidentEntry Entry { get; private set; }

        public bool IsIdle => _path == null || _pathIndex >= _path.Count;

        private List<Vector3> _path;
        private int _pathIndex;
        private float _pauseLeft;

        public void Initialize(ResidentEntry entry)
        {
            Entry = entry;
            ResidentId = entry?.residentId ?? string.Empty;
            _path = null;
            _pathIndex = 0;
            _pauseLeft = 0f;
            transform.position = entry != null ? HomeStand : transform.position;
        }

        public Vector3 HomeStand => Entry != null
            ? Entry.standWorld + new Vector3(Entry.slot * 0.34f, 0f, 0f)
            : transform.position;
        public bool TryAssignPath(List<Vector3> path)
        {
            if (path == null || path.Count < 2 || !IsIdle || _pauseLeft > 0f)
            {
                return false;
            }

            _path = path;
            _pathIndex = 0;
            return true;
        }

        /// <summary>Called by the director. Returns true while the resident is walking.</summary>
        public bool Advance(float deltaTime, float speed)
        {
            if (_pauseLeft > 0f)
            {
                _pauseLeft -= deltaTime;
                if (_pauseLeft <= 0f)
                {
                    _pauseLeft = 0f;
                    transform.position = HomeStand;
                }

                return false;
            }

            if (_path == null || _pathIndex >= _path.Count)
            {
                return false;
            }

            var target = _path[_pathIndex];
            var current = transform.position;
            var toTarget = target - current;
            var step = Mathf.Max(0.01f, speed) * deltaTime;
            if (toTarget.magnitude <= step)
            {
                transform.position = target;
                _pathIndex++;
                if (_pathIndex >= _path.Count)
                {
                    _path = null;
                    _pathIndex = 0;
                    _pauseLeft = 2f + (Entry != null ? Mathf.Abs(Entry.cellIndex % 3) : 0);
                    return false;
                }

                return true;
            }

            transform.position = current + toTarget.normalized * step;
            return true;
        }
    }
}
