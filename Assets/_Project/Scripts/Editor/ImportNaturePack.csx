using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using System.Threading;

var list = Client.List(true);
while (!list.IsCompleted) Thread.Sleep(100);
"listed";
