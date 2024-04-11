using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEditorInternal;
using System.Collections.Generic;

namespace FindCallsFrom
{
    public class FindCallsFromWindow : EditorWindow
    {
        [MenuItem("Window/Analysis/FindCallsFrom")]
        static void ShowFindCallsFrom()
        {
            GetWindow<FindCallsFromWindow>();
        }

        public string checkMarkerName = "GC.Alloc";
        Dictionary<int, int> callCounts = new Dictionary<int, int>();

        private void OnGUI()
        {
            string newMarkerName = EditorGUILayout.TextField("MarkerName", checkMarkerName);
            if(newMarkerName != checkMarkerName)
            {
                checkMarkerName = newMarkerName;
            }
            if (GUILayout.Button("Analyze"))
            {
                PullFromProfiler();
            }
        }

        void PullFromProfiler()
        {
            callCounts.Clear();
            Assembly assem = typeof(Editor).Assembly;
            Type profilerWindowType = assem.GetType("UnityEditor.ProfilerWindow");

            FieldInfo currentFrameFieldInfo = profilerWindowType.GetField("m_CurrentFrame", BindingFlags.NonPublic | BindingFlags.Instance);

            EditorWindow profilerWindow = null;
            UnityEngine.Object[] windows = Resources.FindObjectsOfTypeAll(profilerWindowType);
            if (windows != null && windows.Length > 0)
                profilerWindow = (EditorWindow)windows[0];

            if (profilerWindow == null)
            {
                return;
            }

            int first, last;
            GetFrameRangeFromProfiler(profilerWindow, out first, out last);

            ProfileData profileData = GetData(first - 1, last + 1);

            int nameIndex = -1;
            int curIndex = 0;
            foreach(var markerName in profileData.markerNames)
            {
                if (markerName.Equals(checkMarkerName))
                {
                    nameIndex = curIndex;
                    break;
                }
                ++curIndex;
            }

            if(curIndex == -1)
            {
                return;
            }

            Dictionary<int, ProfileMarker> callHierarchy = new Dictionary<int, ProfileMarker>();
            foreach (var frame in profileData.frames)
            {
                foreach(var thread in frame.threads)
                {
                    callHierarchy.Clear();
                    foreach (var marker in thread.markers)
                    {
                        if (callHierarchy.ContainsKey(marker.depth))
                        {
                            callHierarchy[marker.depth] = marker;
                        }
                        else
                        {
                            callHierarchy.Add(marker.depth, marker);
                        }
                        
                        if (marker.nameIndex == nameIndex)
                        {
                            ProfileMarker caller = null;
                            if (callHierarchy.TryGetValue(marker.depth - 1, out caller))
                            {
                                int count = 0;
                                if (callCounts.TryGetValue(caller.nameIndex, out count))
                                {
                                    callCounts[caller.nameIndex] = count + 1;
                                }
                                else
                                {
                                    callCounts.Add(caller.nameIndex, 1);
                                }
                            }
                            
                        }
                    }
                }
            }

            string output = "";
            foreach(var callInfo in callCounts)
            {
                output += $"\"{profileData.markerNames[callInfo.Key]}\", \"{callInfo.Value}\"\r\n";
            }
            
            using(var streamWriter = new System.IO.StreamWriter("callinfo.csv"))
            {
                streamWriter.Write(output);
            }
        }

        public bool GetFrameRangeFromProfiler(EditorWindow profilerWindow, out int first, out int last)
        {
            if (profilerWindow)
            //if (ProfilerDriver.enabled)
            {
                first = 1 + ProfilerDriver.firstFrameIndex;
                last = 1 + ProfilerDriver.lastFrameIndex;
                // Clip to the visible frames in the profile which indents 1 in from end
                if (first < last)
                    last--;
                return true;
            }

            first = 1;
            last = 1;
            return false;
        }

        ProfileData GetData(int firstFrameIndex, int lastFrameIndex)
        {
            //s_UseRawIterator ^= true;

            System.Diagnostics.Stopwatch stopwatch = new System.Diagnostics.Stopwatch();
            stopwatch.Start();

            ProfileData data = GetDataRaw(firstFrameIndex, lastFrameIndex);

            stopwatch.Stop();
            //Debug.LogFormat("Pull time {0}ms ({1})", stopwatch.ElapsedMilliseconds, s_UseRawIterator ? "Raw" : "Standard");

            return data;
        }

        ProfileData GetDataRaw(int firstFrameIndex, int lastFrameIndex)
        {
            EditorUtility.DisplayProgressBar("GetDataRaw", "", 0f);
            bool firstError = true;

            var data = new ProfileData();
            data.SetFrameIndexOffset(firstFrameIndex);

            var depthStack = new Stack<int>();

            var threadNameCount = new Dictionary<string, int>();
            var threadIdMapping = new Dictionary<ulong, string>();
            var markerIdToNameIndex = new Dictionary<int, int>();
            for (int frameIndex = firstFrameIndex; frameIndex <= lastFrameIndex; ++frameIndex)
            {
                EditorUtility.DisplayProgressBar("GetDataRaw", "", (frameIndex - firstFrameIndex)/(float)(lastFrameIndex - firstFrameIndex));
                int threadIndex = 0;

                bool threadValid = true;
                threadNameCount.Clear();
                ProfileFrame frame = null;
                while (threadValid)
                {
                    using (RawFrameDataView frameData = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex))
                    {
                        if (threadIndex == 0)
                        {
                            frame = new ProfileFrame();
                            if (frameData.valid)
                            {
                                frame.msStartTime = frameData.frameStartTimeMs;
                                frame.msFrame = frameData.frameTimeMs;
                            }
                            data.Add(frame);
                        }

                        if (!frameData.valid)
                            break;

                        string threadNameWithIndex;

                        if (threadIdMapping.ContainsKey(frameData.threadId))
                        {
                            threadNameWithIndex = threadIdMapping[frameData.threadId];
                        }
                        else
                        {
                            string threadName = frameData.threadName;
                            if (threadName.Trim() == "")
                            {
                                Debug.Log(string.Format("Warning: Unnamed thread found on frame {0}. Corrupted data suspected, ignoring frame", frameIndex));
                                threadIndex++;
                                continue;
                            }
                            var groupName = frameData.threadGroupName;
                            threadName = ProfileData.GetThreadNameWithGroup(threadName, groupName);

                            int nameCount = 0;
                            threadNameCount.TryGetValue(threadName, out nameCount);
                            threadNameCount[threadName] = nameCount + 1;

                            threadNameWithIndex = ProfileData.ThreadNameWithIndex(threadNameCount[threadName], threadName);

                            threadIdMapping[frameData.threadId] = threadNameWithIndex;
                        }

                        var thread = new ProfileThread();
                        data.AddThreadName(threadNameWithIndex, thread);

                        frame.Add(thread);

                        // The markers are in depth first order 
                        depthStack.Clear();
                        // first sample is the thread name
                        for (int i = 1; i < frameData.sampleCount; i++)
                        {
                            float durationMS = frameData.GetSampleTimeMs(i);
                            int markerId = frameData.GetSampleMarkerId(i);
                            if (durationMS < 0)
                            {
                                if (firstError)
                                {
                                    int displayIndex = data.OffsetToDisplayFrame(frameIndex);
                                    string threadName = frameData.threadName;

                                    string name = frameData.GetSampleName(i);
                                    Debug.LogFormat("Ignoring Invalid marker time found for {0} on frame {1} on thread {2} ({3} < 0)",
                                            name, displayIndex, threadName, durationMS);

                                    firstError = false;
                                }
                            }
                            else
                            {
                                int depth = 1 + depthStack.Count;
                                var markerData = ProfileMarker.Create(durationMS, depth);

                                // Use name index directly if we have already stored this named marker before
                                int nameIndex;
                                if (markerIdToNameIndex.TryGetValue(markerId, out nameIndex))
                                {
                                    markerData.nameIndex = nameIndex;
                                }
                                else
                                {
                                    string name = frameData.GetSampleName(i);
                                    data.AddMarkerName(name, markerData);
                                    markerIdToNameIndex[markerId] = markerData.nameIndex;
                                }

                                thread.Add(markerData);
                            }

                            int childrenCount = frameData.GetSampleChildrenCount(i);
                            if (childrenCount > 0)
                            {
                                depthStack.Push(childrenCount);
                            }
                            else
                            {
                                while (depthStack.Count > 0)
                                {
                                    int remainingChildren = depthStack.Pop();
                                    if (remainingChildren > 1)
                                    {
                                        depthStack.Push(remainingChildren - 1);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    threadIndex++;
                }
            }

            data.Finalise();

            EditorUtility.ClearProgressBar();

            return data;
        }
    }
}