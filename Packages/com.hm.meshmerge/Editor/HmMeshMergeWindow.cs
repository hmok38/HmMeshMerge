using System.Collections.Generic;
using System.IO;
using HmMeshMerge;
using UnityEditor;
using UnityEngine;

namespace HmMeshMergeEditor
{
    /// <summary>
    /// 合并窗口：绑定一份合并配置资产（HmMeshMergeAsset），直接编辑它的源列表、参数表与通道设置。
    /// 窗口本身不保存设置，避免出现配置的第二份副本；合并结果也写回同一资产。
    /// </summary>
    internal sealed class HmMeshMergeWindow : EditorWindow
    {
        [SerializeField] private HmMeshMergeAsset _asset;

        private SerializedObject _serialized;
        private SerializedProperty _sourcesProperty;
        private Vector2 _scroll;
        private readonly List<string> _errors = new List<string>();
        private string _result = string.Empty;

        [MenuItem("Tools/HmMeshMerge/网格合并")]
        private static void Open()
        {
            GetWindow<HmMeshMergeWindow>("网格合并");
        }

        private void OnEnable()
        {
            BindAsset();
        }

        private void OnDisable()
        {
            FlushEdits();
            _serialized?.Dispose();
            _serialized = null;
        }

        /// <summary>提交窗口编辑并写入磁盘；没有实际改动时资产库不会重写文件。</summary>
        private void FlushEdits()
        {
            _serialized?.ApplyModifiedProperties();
            if (_asset != null)
            {
                AssetDatabase.SaveAssetIfDirty(_asset);
            }
        }

        private void OnGUI()
        {
            if (_asset == null)
            {
                DrawNoAsset();
                return;
            }

            _serialized.Update();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawAssetSlot();
            DrawSourceList();
            DrawParameterTable();
            DrawSettings();
            DrawActions();
            DrawResults();
            EditorGUILayout.EndScrollView();
            FlushEdits();
        }

        /// <summary>未绑定配置时只提供新建与选择两条入口。</summary>
        private void DrawNoAsset()
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "请先新建或选择一个合并配置资产。配置与合并结果保存在该资产中。",
                MessageType.Info);

            if (GUILayout.Button("新建配置资产", GUILayout.Height(28)))
            {
                CreateAsset();
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();
            var selected = (HmMeshMergeAsset)EditorGUILayout.ObjectField(
                "选择已有配置", null, typeof(HmMeshMergeAsset), false);
            if (EditorGUI.EndChangeCheck() && selected != null)
            {
                FlushEdits();
                _asset = selected;
                BindAsset();
                GUIUtility.ExitGUI();
            }
        }

        private void CreateAsset()
        {
            string path = EditorUtility.SaveFilePanelInProject("新建合并配置", "HmMeshMerge", "asset", "选择保存位置");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            FlushEdits();
            var asset = ScriptableObject.CreateInstance<HmMeshMergeAsset>();
            asset.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            _asset = asset;
            BindAsset();
        }

        private void BindAsset()
        {
            _serialized?.Dispose();
            _serialized = _asset == null ? null : new SerializedObject(_asset);
            _sourcesProperty = _serialized == null ? null : _serialized.FindProperty(HmMeshMergeAsset.SOURCES_FIELD);
            _errors.Clear();
            _result = string.Empty;
            if (_asset == null)
            {
                Debug.Log("[HmMeshMerge] 请先新建或选择一个合并配置资产。配置与合并结果保存在该资产中。");
            }
        }

        private void DrawAssetSlot()
        {
            EditorGUILayout.LabelField("配置", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            var selected = (HmMeshMergeAsset)EditorGUILayout.ObjectField(
                "合并配置", _asset, typeof(HmMeshMergeAsset), false);
            if (EditorGUI.EndChangeCheck() && selected != _asset)
            {
                FlushEdits();
                _asset = selected;
                BindAsset();
                GUIUtility.ExitGUI();
                return;
            }

            if (GUILayout.Button("新建配置资产", GUILayout.Height(20)))
            {
                CreateAsset();
                GUIUtility.ExitGUI();
                return;
            }

            EditorGUILayout.LabelField("输出位置", AssetDatabase.GetAssetPath(_asset));
            EditorGUILayout.Space();
        }

        private void DrawSourceList()
        {
            EditorGUILayout.LabelField("源（顺序即来源索引）", EditorStyles.boldLabel);
            int removeIndex = -1;
            for (int i = 0; i < _sourcesProperty.arraySize; i++)
            {
                SerializedProperty element = _sourcesProperty.GetArrayElementAtIndex(i);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(i.ToString(), GUILayout.Width(32));
                EditorGUILayout.PropertyField(element.FindPropertyRelative(nameof(HmMeshMergeSource.mesh)), GUIContent.none);
                EditorGUILayout.PropertyField(element.FindPropertyRelative(nameof(HmMeshMergeSource.material)), GUIContent.none);
                if (GUILayout.Button("移除", GUILayout.Width(48)))
                {
                    removeIndex = i;
                }

                EditorGUILayout.EndHorizontal();
            }

            if (removeIndex >= 0)
            {
                int sizeBefore = _sourcesProperty.arraySize;
                _sourcesProperty.DeleteArrayElementAtIndex(removeIndex);
                if (_sourcesProperty.arraySize == sizeBefore)
                {
                    // 引用类型元素第一次删除只是清空，需要再删一次。
                    _sourcesProperty.DeleteArrayElementAtIndex(removeIndex);
                }
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("添加来源"))
            {
                int index = _sourcesProperty.arraySize++;
                SerializedProperty element = _sourcesProperty.GetArrayElementAtIndex(index);
                element.FindPropertyRelative(nameof(HmMeshMergeSource.mesh)).objectReferenceValue = null;
                element.FindPropertyRelative(nameof(HmMeshMergeSource.material)).objectReferenceValue = null;
            }

            if (GUILayout.Button("添加所选网格"))
            {
                AddSelectedMeshes();
            }

            if (GUILayout.Button("清空源"))
            {
                _sourcesProperty.arraySize = 0;
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>参数表的每一行是一份参数；停用只把该行置空，行号留给后面的参数不动。</summary>
        private void DrawParameterTable()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("参数表（行号稳定；停用即空出该行）", EditorStyles.boldLabel);
            SerializedProperty table = _serialized.FindProperty(HmMeshMergeAsset.PARAMETERS_FIELD);
            for (int i = 0; i < table.arraySize; i++)
            {
                SerializedProperty entry = table.GetArrayElementAtIndex(i);
                SerializedProperty name = entry.FindPropertyRelative(nameof(HmMeshMergeParameterEntry.propertyName));
                SerializedProperty row = entry.FindPropertyRelative(nameof(HmMeshMergeParameterEntry.row));
                SerializedProperty active = entry.FindPropertyRelative(nameof(HmMeshMergeParameterEntry.active));

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(row.intValue.ToString(), GUILayout.Width(32));
                // 已分配的属性名不允许改写，否则旧 Shader 的行号契约会被重新解释。
                EditorGUILayout.LabelField(name.stringValue);
                active.boolValue = EditorGUILayout.ToggleLeft("启用", active.boolValue, GUILayout.Width(52));
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("按源着色器列出属性"))
            {
                FillParametersFromShader(table);
            }

            if (GUILayout.Button("重新整理参数表"))
            {
                CompactParameterTable(table);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void FillParametersFromShader(SerializedProperty table)
        {
            FlushEdits();
            if (_asset.Sources.Count == 0 || _asset.Sources[0] == null || _asset.Sources[0].material == null)
            {
                Report("请先添加源，且第一个源要带材质，才能列出它的着色器属性。", true);
                return;
            }

            Shader shader = _asset.Sources[0].material.shader;
            foreach (HmMeshMergeSource source in _asset.Sources)
            {
                if (source == null || source.material == null || shader == null || source.material.shader != shader)
                {
                    Report("请先为每个来源指定使用同一 Shader 的材质，再刷新参数表。", true);
                    return;
                }
            }

            List<HmMeshMergeParameterEntry> generated =
                HmMeshMergeParameterWriter.RefreshTable(_asset.Sources, _asset.Parameters);
            _serialized.Update();
            table = _serialized.FindProperty(HmMeshMergeAsset.PARAMETERS_FIELD);
            table.arraySize = generated.Count;
            for (int i = 0; i < generated.Count; i++)
            {
                SerializedProperty entry = table.GetArrayElementAtIndex(i);
                entry.FindPropertyRelative(nameof(HmMeshMergeParameterEntry.propertyName)).stringValue = generated[i].propertyName;
                entry.FindPropertyRelative(nameof(HmMeshMergeParameterEntry.row)).intValue = generated[i].row;
                entry.FindPropertyRelative(nameof(HmMeshMergeParameterEntry.active)).boolValue = generated[i].active;
            }

            FlushEdits();
            Report("已刷新属性：已有行号与选择保留；新属性追加。首次按差异排序并默认启用不同项。", false);
        }

        /// <summary>丢弃停用的行并重新编号；使用者已复制到自有着色器里的行号会随之失效。</summary>
        private void CompactParameterTable(SerializedProperty table)
        {
            const string MESSAGE = "重新整理将删除停用行并重新编号，已复制到 Shader 的行号需要同步修改。";
            Debug.LogWarning("[HmMeshMerge] " + MESSAGE, _asset);
            if (!EditorUtility.DisplayDialog("重新整理参数表", MESSAGE, "重新整理", "取消"))
            {
                Report("已取消参数表重排。", false);
                return;
            }

            var names = new List<string>();
            for (int i = 0; i < table.arraySize; i++)
            {
                SerializedProperty entry = table.GetArrayElementAtIndex(i);
                if (entry.FindPropertyRelative(nameof(HmMeshMergeParameterEntry.active)).boolValue)
                {
                    names.Add(entry.FindPropertyRelative(nameof(HmMeshMergeParameterEntry.propertyName)).stringValue);
                }
            }

            table.arraySize = names.Count;
            for (int i = 0; i < names.Count; i++)
            {
                SerializedProperty entry = table.GetArrayElementAtIndex(i);
                entry.FindPropertyRelative(nameof(HmMeshMergeParameterEntry.propertyName)).stringValue = names[i];
                entry.FindPropertyRelative(nameof(HmMeshMergeParameterEntry.row)).intValue = i;
                entry.FindPropertyRelative(nameof(HmMeshMergeParameterEntry.active)).boolValue = true;
            }

            Report("参数表已重排为连续行号；使用者已复制到自有着色器里的行号会随之失效。", false);
        }

        private void DrawSettings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("设置", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_serialized.FindProperty(nameof(HmMeshMergeAsset.indexChannel)),
                new GUIContent("索引通道", "默认 UV3（TEXCOORD3），必须是所有源网格的空闲通道。"));
            EditorGUILayout.PropertyField(_serialized.FindProperty(nameof(HmMeshMergeAsset.outputShader)),
                new GUIContent("输出 Shader", "留空时生成 URP 无光照接入模板；也可指定已接入数据契约的 Shader。"));
        }

        private void DrawActions()
        {
            EditorGUILayout.Space();
            if (GUILayout.Button("执行合并", GUILayout.Height(28)))
            {
                Execute();
            }
        }

        private void AddSelectedMeshes()
        {
            foreach (Object selected in Selection.objects)
            {
                if (selected is not Mesh mesh)
                {
                    continue;
                }

                int index = _sourcesProperty.arraySize;
                _sourcesProperty.InsertArrayElementAtIndex(index);
                SerializedProperty element = _sourcesProperty.GetArrayElementAtIndex(index);
                element.FindPropertyRelative(nameof(HmMeshMergeSource.mesh)).objectReferenceValue = mesh;
                element.FindPropertyRelative(nameof(HmMeshMergeSource.material)).objectReferenceValue = null;
            }
        }

        private void Execute()
        {
            FlushEdits();
            _errors.Clear();
            _result = string.Empty;
            try
            {
                HmMeshMergeBuilder.Build(_asset);
                Report($"已生成：{AssetDatabase.GetAssetPath(_asset)}", false);
            }
            catch (System.Exception exception)
            {
                Report(exception.Message, true);
                // 校验与生成失败的消息已由 Report 完整输出，不重复打印；其他异常补记堆栈便于定位。
                if (exception is not System.InvalidOperationException)
                {
                    Debug.LogException(exception, _asset);
                }
            }
            finally
            {
                _serialized.Update();
            }
        }

        /// <summary>记录一条消息并同步到 Console；窗口只负责展示，不重复输出。</summary>
        private void Report(string message, bool isError)
        {
            if (isError)
            {
                _errors.Add(message);
                Debug.LogError($"[HmMeshMerge] {message}", _asset);
                return;
            }

            _result = message;
            Debug.Log($"[HmMeshMerge] {message}", _asset);
        }

        private void DrawResults()
        {
            foreach (string error in _errors)
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }

            if (!string.IsNullOrEmpty(_result))
            {
                EditorGUILayout.HelpBox(_result, MessageType.Info);
            }
        }
    }
}
