using UnityEditor;
using UnityEngine;
using System.IO;
using System.Collections.Generic;
using FunctionCallTracker;
using static UnityEditor.AddressableAssets.Settings.AddressableAssetProfileSettings;

namespace IL2CPPOutputFileAnalyzer
{
    public class IL2CPPOutputFileAnalyzerWindow : EditorWindow
    {
        [MenuItem("Window/Analysis/IL2CPPOutputFileAnalyzer")]
        static void ShowIL2CPPOutputFileAnalyzer()
        {
            GetWindow<IL2CPPOutputFileAnalyzerWindow>();
        }

        class ModuleInfo
        {
            public string name;
            public int generatedMethods = 0;
        }

        public string il2cppOutPath = "";

        private void OnGUI()
        {
            string newil2cppOutPath = EditorGUILayout.TextField("Path", il2cppOutPath);
            if (newil2cppOutPath != il2cppOutPath)
            {
                il2cppOutPath = newil2cppOutPath;
            }
            if (GUILayout.Button("Analyze"))
            {
                List<ModuleInfo> moduleInfos = new List<ModuleInfo>();
                if(Directory.Exists(il2cppOutPath) == false)
                {
                    return;
                }

                string[] files = Directory.GetFiles(il2cppOutPath);

                foreach (string file in files)
                {
                    var info = ParsingFile(file);
                    if (info != null)
                    {
                        moduleInfos.Add(info);
                    }
                }

                if(moduleInfos.Count == 0)
                {
                    return;
                }

                string output = "";
                foreach (var moduleInfo in moduleInfos)
                {
                    output += $"\"{moduleInfo.name}\", \"{moduleInfo.generatedMethods}\"\r\n";
                }

                using (var streamWriter = new System.IO.StreamWriter("moduleInfo.csv"))
                {
                    streamWriter.Write(output);
                }
            }
        }

        ModuleInfo ParsingFile(string path)
        {
            if(path.EndsWith("_CodeGen.c") == false || File.Exists(path) == false) 
            {
                return null;
            }

            ModuleInfo moduleInfo = new ModuleInfo();
            string prevLine = "";
            using(var stream = new StreamReader(path))
            {
                while (stream.EndOfStream == false)
                {
                    string line = stream.ReadLine();
                    if(line == null)
                    {
                        break;
                    }

                    if(line.StartsWith("const Il2CppCodeGenModule"))
                    {
                        line = stream.ReadLine();
                        line = stream.ReadLine();
                        moduleInfo.name = line.Trim().Replace("\"","").Trim(',');
                    }
                    else if (string.IsNullOrEmpty(moduleInfo.name) == false && line.Contains("s_methodPointers"))
                    {
                        moduleInfo.generatedMethods = int.Parse(prevLine.Trim().Trim(','));
                        break;
                    }

                    prevLine = line;
                }
                
            }
            return moduleInfo;
        }
    }
}
