﻿//#define PROFILE
//#define DBG

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Vexe.Editor.GUIs;
using Vexe.Editor.Internal;
using Vexe.Editor.Types;
using Vexe.Editor.Visibility;
using Vexe.Runtime.Extensions;
using Vexe.Runtime.Helpers;
using Vexe.Runtime.Types;
using UnityObject = UnityEngine.Object;

namespace Vexe.Editor.Editors
{
    using Editor = UnityEditor.Editor;

    public abstract class BaseEditor : Editor
    {
        /// <summary>
        /// Whether or not to show the script field header
        /// </summary>
        public bool ShowScriptHeader = true;

        /// <summary>
        /// A unique identifier that we get from the target object this editor is inspecting
        /// Mainly used for storing and persisting editor-only values in prefs objects (such as BetterPrefs)
        /// Note that when inspecting a Better[Behaviour|ScriptableObject] we get the id from their 'Id' property
        /// </summary>
        protected int id;

        /// <summary>
        /// The gui wrapper object of use to draw things.
        /// Recall there's 2 types:
        /// RabbitGUI (a fast custom gui layout system) which is meant to be the standard choice
        /// and TurtleGUI (wraps EditorGUILayout) meant as a fallback type/ worst case scenario/ last choice type of deal
        /// </summary>
        protected BaseGUI gui;

        /// <summary>
        /// The runtime type of the target object we're inspecting (the type always 'is a' Component)
        /// </summary>
        protected Type targetType;

        /// <summary>
        /// The gameObject our target Component is attached to
        /// </summary>
        protected GameObject gameObject;

        /// <summary>
        /// Storage asset for editor-only settings/values
        /// </summary>
        protected static BetterPrefs prefs;

        /// <summary>
        /// A handly little wrapper for prefs.Bools - mainly used for setting foldout values
        /// So instead of saying prefs.Bools[key] = value; we just say foldouts[key] = value;
        /// And instead of: var value = prefs.Bools.ValueOrDefault(key); we just: var value = foldouts[key];
        /// </summary>
        protected static Foldouts foldouts;

        private List<MembersCategory> _categories;
        private List<MemberInfo> _visibleMembers;
        private SerializedProperty _script;
        private EditorMember _serializationData, _debug, _serializerType;
        private bool _useUnityGUI;
        private int _repaintCount, _spacing;
        private CategoryDisplay _display;
        private Action _onGUIFunction;
        private string[] _membersDrawnByUnityLayout;
        static int guiKey = "UnityGUI".GetHashCode();

        /// <summary>
        /// Members of these types will be drawn by Unity's Layout system
        /// </summary>
        private static readonly Type[] DrawnByUnityTypes = new Type[]
        {
            typeof(UnityEngine.Events.UnityEventBase)
        };

        private static bool useUnityGUI
        {
            get { return prefs.Bools.ValueOrDefault(guiKey); }
            set { prefs.Bools[guiKey] = value; }
        }

        protected bool foldout
        {
            get { return foldouts[id]; }
            set { foldouts[id] = value; }
        }

        private void OnEnable()
        {
            if (prefs == null)
                prefs = BetterPrefs.GetEditorInstance();

            if (foldouts == null)
                foldouts = new Foldouts(prefs);

            var component = target as Component;
            gameObject = component == null ? null : component.gameObject;

            targetType = target.GetType();

            id = RuntimeHelper.GetTargetID(target);

            _useUnityGUI = useUnityGUI;
            gui = _useUnityGUI ? (BaseGUI)new TurtleGUI() : new RabbitGUI();

            Initialize();

            gui.OnEnable();
        }

        private void OnDisable()
        {
            gui.OnDisable();
        }

        /// <summary>
        /// Call this if you're inlining this editor and you're using your own gui instance
        /// It's important in that case to use the same gui in order to have the same layout
        /// </summary>
        public void OnInlinedGUI(BaseGUI otherGui)
        {
            this.gui = otherGui;
            OnGUI();
        }

        public sealed override void OnInspectorGUI()
        {
            // update gui instance if it ever changes
            if (_useUnityGUI != useUnityGUI)
            {
                _useUnityGUI = useUnityGUI;
                gui = _useUnityGUI ? (BaseGUI)new TurtleGUI() : new RabbitGUI();
            }

            // creating the delegate once, reducing allocation
            if (_onGUIFunction == null)
                _onGUIFunction = OnGUI;

            var rabbit = gui as RabbitGUI;
            if (rabbit != null && rabbit.OnFinishedLayoutReserve == null && _membersDrawnByUnityLayout.Length > 0)
                rabbit.OnFinishedLayoutReserve = DoUnityLayout;

            // I found 25 to be a good padding value such that there's not a whole lot of empty space wasted
            // and the vertical inspector scrollbar doesn't obstruct our controls
            gui.OnGUI(_onGUIFunction, new Vector2(0f, 25f), id);

            // addresses somes cases of editor slugishness when selecting gameObjects
            if (_repaintCount < 3)
            {
                _repaintCount++;
                Repaint();
            }
        }

        private void DoUnityLayout()
        {
            serializedObject.Update();

            for(int i = 0; i < _membersDrawnByUnityLayout.Length; i++)
            {
                var memberName = _membersDrawnByUnityLayout[i];
                var property = serializedObject.FindProperty(memberName);
                if (property == null)
                {
                    Debug.Log("Member cannot be drawn by Unity: " + memberName);
                    continue;
                }

                EditorGUI.BeginChangeCheck();

                EditorGUILayout.PropertyField(property, true);

                if (EditorGUI.EndChangeCheck())
                { 
                    var bb = target as BetterBehaviour;
                    if (bb != null)
                        bb.DelayNextDeserialize();
                    else
                    { 
                        var bso = target as BetterScriptableObject;
                        if (bso != null)
                        bso.DelayNextDeserialize();
                    }
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        protected static void LogFormat(string msg, params object[] args)
        {
            Debug.Log(string.Format(msg, args));
        }

        protected static void Log(object msg)
        {
            Debug.Log(msg);
        }

        /// <summary>
        /// Called before internal initialization happens (we're just about to fetch and assign members to their corresponding categories etc)
        /// </summary>
        protected virtual void OnBeforeInitialized() { }

        /// <summary>
        /// Called after internal initialization is finished (we're done assigning members and allocating categories)
        /// </summary>
        protected virtual void OnAfterInitialized() { }

        /// <summary>
        /// Fetches visible members in the inspected target object and assigns them to their corresponding categories
        /// </summary>
        private void Initialize()
        {
            OnBeforeInitialized();

            // fetch visible members
            var vfwObj = target as IVFWObject;
            Assert.NotNull(vfwObj, "Target must implement IVFWObject!");
            _visibleMembers = VisibilityLogic.CachedGetVisibleMembers(targetType, vfwObj.GetSerializationLogic());

            var drawnByUnity = _visibleMembers
                .Where(x => x.IsDefined<DrawByUnityAttribute>() || DrawnByUnityTypes.Any(x.GetDataType().IsA));

            _visibleMembers = _visibleMembers.Except(drawnByUnity).ToList();

            _membersDrawnByUnityLayout = drawnByUnity.Select(x => x.Name).ToArray();

            // allocate categories
            _categories = new List<MembersCategory>();

            var multiple	= targetType.GetCustomAttribute<DefineCategoriesAttribute>(true);
            var definitions = targetType.GetCustomAttributes<DefineCategoryAttribute>(true);
            if (multiple != null)
                definitions = definitions.Concat(multiple.names.Select(n => new DefineCategoryAttribute(n, 1000)));

            Func<string, string[]> ParseCategoryPath = fullPath =>
            {
                int nPaths = fullPath.Split('/').Length;
                string[] result = new string[nPaths];
                for (int i = 0, index = -1; i < nPaths - 1; i++)
                {
                    index = fullPath.IndexOf('/', index + 1);
                    result[i] = fullPath.Substring(0, index);
                }
                result[nPaths - 1] = fullPath;
                return result;
            };

            // Order by exclusivity
            var defs = from d in definitions
                       let paths = ParseCategoryPath(d.FullPath)
                       orderby !d.Exclusive
                       select new { def = d, paths };

            // Parse paths and resolve definitions
            var resolver = new CategoryDefinitionResolver();
            var lookup = new Dictionary<string, MembersCategory>();
            foreach (var x in defs)
            {
                var paths = x.paths;
                var d = x.def;

                MembersCategory parent = null;

                for (int i = 0; i < paths.Length; i++)
                {
                    var path = paths[i];

                    var current = (parent == null ?  _categories :
                        parent.NestedCategories).FirstOrDefault(c => c.FullPath == path);

                    if (current == null)
                    {
                        current = new MembersCategory(path, d.DisplayOrder, id);
                        if (i == 0)
                            _categories.Add(current);
                        if (parent != null)
                            parent.NestedCategories.Add(current);
                    }
                    lookup[path] = current;
                    parent = current;
                }

                var last = lookup[paths.Last()];
                last.ForceExpand = d.ForceExpand;
                last.AlwaysHideHeader = d.AlwaysHideHeader;
                resolver.Resolve(_visibleMembers, d).Foreach(last.Members.Add);

                lookup.Clear();
                parent.Members = parent.Members.OrderBy<MemberInfo, float>(VisibilityLogic.GetMemberDisplayOrder).ToList();
            }

            // filter out empty categories
            _categories = _categories.Where(x => x.NestedCategories.Count > 0 || x.Members.Count > 0)
                                     .OrderBy(x => x.DisplayOrder)
                                     .ToList();

            for (int i = 0; i < _categories.Count; i++)
            {
                var c = _categories[i];
                c.RemoveEmptyNestedCategories();
            }

            var displayKey = RuntimeHelper.CombineHashCodes(id, "display");
            var displayValue = prefs.Ints.ValueOrDefault(displayKey, -1);
            var vfwSettings = VFWSettings.GetInstance();
            _display = displayValue == -1 ? vfwSettings.DefaultDisplay : (CategoryDisplay)displayValue;
            prefs.Ints[displayKey] = (int)_display;

            var spacingKey = RuntimeHelper.CombineHashCodes(id, "spacing");
            _spacing = prefs.Ints.ValueOrDefault(spacingKey, vfwSettings.DefaultSpacing);
            prefs.Ints[spacingKey] = _spacing;

            var field = targetType.GetMemberFromAll("_serializationData", Flags.InstancePrivate);
            if (field == null)
                throw new vMemberNotFound(targetType, "_serializationData");

            _serializationData = EditorMember.WrapMember(field, target, target, id);

            field = targetType.GetField("dbg", Flags.InstanceAnyVisibility);
            if (field == null)
                throw new vMemberNotFound(targetType, "dbg");

            _debug = EditorMember.WrapMember(field, target, target, id);

            var serializerType = targetType.GetMemberFromAll("SerializerType", Flags.InstanceAnyVisibility);
            if (serializerType == null)
                throw new vMemberNotFound(targetType, "SerializerType");

            _serializerType = EditorMember.WrapMember(serializerType, target, target, id);

            OnAfterInitialized();
        }

        protected virtual void OnGUI()
        {
            if (ShowScriptHeader)
            {
                var scriptKey = RuntimeHelper.CombineHashCodes(id, "script");
                gui.Space(3f);
                using (gui.Horizontal(EditorStyles.toolbarButton))
                {
                    gui.Space(10f);
                    foldouts[scriptKey] = gui.Foldout(foldouts[scriptKey]);
                    gui.Space(-12f);

                    if (ScriptField()) // script changed? exit!
                        return;
                }

                if (foldouts[scriptKey])
                {
                    gui.Space(2f);

                    using (gui.Indent(GUI.skin.textField))
                    {
                        gui.Space(3f);
                        if (targetType.IsDefined<HasRequirementsAttribute>())
                        {
                            using (gui.Horizontal())
                            {
                                gui.Space(3f);
                                if (gui.MiniButton("Resolve Requirements", (Layout)null))
                                    Requirements.Resolve(target, gameObject);
                            }
                        }

                        gui.Member(_debug);

                        var mask = gui.BunnyMask("Display", _display);
                        {
                            var newValue = (CategoryDisplay)mask;
                            if (_display != newValue)
                            {
                                _display = newValue;
                                var displayKey = RuntimeHelper.CombineHashCodes(id, "display");
                                prefs.Ints[displayKey] = mask;
                            }
                        }

                        var spacing = Mathf.Clamp(gui.Int("Spacing", _spacing), -13, (int)EditorGUIUtility.currentViewWidth / 4);
                        if (_spacing != spacing)
                        {
                            _spacing = spacing;
                            prefs.Ints[id + "spacing".GetHashCode()] = _spacing;
                            gui.RequestResetIfRabbit();
                        }

                        gui.Member(_serializerType);

                        gui.Member(_serializationData, true);
                    }
                }
            }

            gui.BeginCheck();

            for (int i = 0; i < _categories.Count; i++)
            {
                var cat     = _categories[i];
                cat.Display = _display;
                cat.Spacing = _spacing;
                cat.gui = gui;
                cat.HideHeader = (_display & CategoryDisplay.Headers) == 0;
                if ((_display & CategoryDisplay.CategorySplitter) != 0)
                    gui.Splitter();
                cat.Draw(target);
            }

            if (gui.HasChanged())
            {
#if DBG
                Log("Target changed: " + target);
#endif
                EditorUtility.SetDirty(target);
                //SerializationManager.MarkModified(target);
            }
        }

        private bool ScriptField()
        {
            serializedObject.Update();

            _script = serializedObject.FindProperty("m_Script");

            EditorGUI.BeginChangeCheck();
            _script.objectReferenceValue = gui.Object("Script", _script.objectReferenceValue, typeof(MonoScript), false);
            gui.Space(5f);
            if (EditorGUI.EndChangeCheck())
            {
                var sel = Selection.objects;
                Selection.objects = new UnityObject[0];
                EditorApplication.delayCall += () => Selection.objects = sel;
                serializedObject.ApplyModifiedProperties();
                return true;
            }

            return false;
        }

        public static class MenuItems
        {
            [MenuItem("Tools/Vexe/GUI/UseUnityGUI")]
            public static void UseUnityGUI()
            {
                BetterPrefs.GetEditorInstance().Bools[guiKey] = true;
            }

            [MenuItem("Tools/Vexe/GUI/UseRabbitGUI")]
            public static void UseRabbitGUI()
            {
                BetterPrefs.GetEditorInstance().Bools[guiKey] = false;
            }
        }
    }
}
