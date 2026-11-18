using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 集合的新建与编辑：两者共用集合信息页里的编辑页（CollectionInfoPanel），不弹对话框。
    /// 这里负责打开编辑页、边输入边校验、位置菜单，以及把整页内容一次性落库。
    /// </summary>
    public partial class MainForm
    {
        // 新建页打开期间的上下文：在哪个分类、当时的搜索词、创建后要一起加入的资源（没有则为 null）。
        private string collectionCreateCategory;
        private string collectionCreateSearch;
        private List<AppInfo> collectionCreateJoin;

        private string SearchTextNow
        {
            get { return search == null ? string.Empty : search.Text ?? string.Empty; }
        }

        /// <summary>
        /// 在资源分类或指定父集合下新建集合：在内容区打开新建页（位置已选好，可再改）。
        /// 新建页已经开着时只换位置，已填的内容保留。
        /// </summary>
        private void CreateCollectionUnder(string parentCollectionKey, List<AppInfo> appsToJoin)
        {
            if (!CanEditCategory(currentCategory) || !collectionSidebarShown) return;
            parentCollectionKey = parentCollectionKey ?? string.Empty;
            ResourceCollection parent;
            string root;
            string parentError;
            if (!ResolveCollectionCreationParent(parentCollectionKey, out parent, out root, out parentError))
            {
                MessageDialog.Show(this, parentError, "新建集合", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<AppInfo> join = null;
            List<string> images = new List<string>();
            if (appsToJoin != null)
            {
                for (int i = 0; i < appsToJoin.Count; i++)
                {
                    AppInfo a = appsToJoin[i];
                    if (a == null || string.IsNullOrEmpty(a.Key) || !CanJoinResourceCategory(a, root)) continue;
                    if (join == null) join = new List<AppInfo>();
                    join.Add(a);
                    if (IsImageMember(a) && images.Count < 60) images.Add(a.TargetPath);
                }
            }

            // 新建页已经开着、这次又没带要加入的资源：只换位置，已填的内容和待加入的资源都保留
            if (collectionInfo.IsCreating && join == null)
            {
                collectionInfo.SetEditLocation(parentCollectionKey);
                return;
            }
            // 正在编辑别的集合且有没保存的改动：先问一句，不直接丢掉
            if (collectionInfo.HasUnsavedEdit && !ConfirmDialog.Show("新建集合",
                "正在编辑的集合还有没保存的修改，新建会放弃这些修改。", "放弃并新建", "继续编辑")) return;
            // 和选中 / 编辑集合一样先退出查看器与播放器，不让它们被新建页盖在下面继续运行
            HideImageViewer();
            HideTextViewer();
            HideArchiveViewer();
            HideVideoPlayer();
            HideMusicPlayer();
            collectionCreateCategory = currentCategory;
            collectionCreateSearch = SearchTextNow;
            collectionCreateJoin = join;
            collectionInfo.Visible = true;
            collectionInfo.BringToFront();
            collectionInfo.BeginCreate(currentCategory, parentCollectionKey, join == null ? 0 : join.Count, images);
            SyncContentVisibility();
        }

        /// <summary>
        /// 新建页开着时的后台刷新（自动重扫、图标回调等）不能把它关掉：只按最新数据重新校验。
        /// 换了分类、搜索词变了或集合侧栏不可用时才放弃新建。返回 true 表示新建页继续保留。
        /// </summary>
        private bool KeepCollectionCreatePage()
        {
            if (!collectionInfo.IsCreating)
            {
                collectionCreateJoin = null;
                return false;
            }
            if (collectionSidebarShown
                && string.Equals(collectionCreateCategory, currentCategory, StringComparison.Ordinal)
                && string.Equals(collectionCreateSearch, SearchTextNow, StringComparison.Ordinal))
            {
                collectionInfo.RefreshValidation();
                return true;
            }
            collectionCreateJoin = null;
            collectionInfo.EndEdit();
            return false;
        }

        /// <summary>
        /// 编辑页点了取消 / 返回 / Esc：编辑页已关闭。新建页占用过信息页的图片列表，
        /// 要恢复它打开前的内容（信息页或网格）；编辑已有集合时信息页数据没动过，原样留着即可。
        /// </summary>
        private void OnCollectionEditCancelled(bool wasCreating)
        {
            if (!wasCreating) return;
            collectionCreateJoin = null;
            UpdateCollectionInfo();
            SyncContentVisibility();
        }

        /// <summary>信息页点「编辑」：信息页已是这个集合的最新数据，原地进入编辑页，不重建列表和侧栏。</summary>
        private void OnCollectionEditRequested()
        {
            ResourceCollection c = FindCollectionByKey(currentCollection);
            if (c != null) collectionInfo.BeginEdit(c.Parent, c.ParentCollection);
        }

        /// <summary>
        /// 编辑页的校验（边输入边调用，保存前再调用一次）：先确认所选位置还在当前分类里，
        /// 再按集合树规则检查名称、重名、层级和数量。返回给用户看的问题说明；null 表示可以保存。
        /// </summary>
        private string ValidateCollectionEditDraft(string name, string parentCollectionKey)
        {
            parentCollectionKey = parentCollectionKey ?? string.Empty;
            ResourceCollection editing = null;
            if (!collectionInfo.IsCreating)
            {
                editing = FindCollectionByKey(currentCollection);
                if (editing == null) return "这个集合已经不存在了，请取消后重试。";
            }
            string root = editing == null ? currentCategory : editing.Parent;
            if (parentCollectionKey.Length > 0)
            {
                ResourceCollection parent = FindCollectionByKey(parentCollectionKey);
                if (parent == null || !string.Equals(parent.Parent, root, StringComparison.Ordinal))
                    return "所选的位置已经不存在了，请重新选一个位置。";
            }
            return CollectionOps.ValidateDraft(config, root, parentCollectionKey, name, editing);
        }

        /// <summary>编辑页「保存 / 创建」：按新建或编辑分别落库。</summary>
        private void OnCollectionEditSave(CollectionEditDraft draft)
        {
            if (draft == null || !collectionInfo.IsEditing) return;
            if (draft.Creating) CommitCollectionCreate(draft);
            else CommitCollectionEdit(draft);
        }

        /// <summary>把粘贴的封面存进受管目录；没有粘贴图时返回所选路径。失败返回 null（已在编辑页提示）。</summary>
        private string ResolveDraftCover(CollectionEditDraft draft)
        {
            if (draft.PastedCover == null) return draft.CoverPath ?? string.Empty;
            string saved = SaveManagedCoverImage(draft.PastedCover);
            if (!string.IsNullOrEmpty(saved)) return saved;
            collectionInfo.ShowEditNotice("封面图片没能保存，请再试一次或换一张图片。", true);
            return null;
        }

        /// <summary>
        /// 新建集合落库：集合定义和要加入的成员在同一次保存里提交。保存失败时配置已整体回滚，
        /// 新建页保留已填内容可直接重试；成功后才更新展开状态和导航。
        /// </summary>
        private void CommitCollectionCreate(CollectionEditDraft draft)
        {
            if (!collectionInfo.IsCreating || !CanEditCategory(currentCategory)) return;
            string root = currentCategory;
            string parentKey = draft.ParentCollection ?? string.Empty;
            string name = (draft.Name ?? string.Empty).Trim();
            if (ValidateCollectionEditDraft(name, parentKey) != null)
            {
                collectionInfo.RefreshValidation();   // 集合树刚变过：把问题显示在名称下方
                return;
            }
            string cover = ResolveDraftCover(draft);
            if (cover == null) return;

            ResourceCollection created = new ResourceCollection();
            created.Parent = root;
            created.ParentCollection = parentKey;
            created.Name = name;
            created.Description = draft.Description ?? string.Empty;
            created.Cover = cover;
            created.CoverFocusX = ClampFocus(draft.FocusX);
            created.CoverFocusY = ClampFocus(draft.FocusY);
            string key = CollectionKeys.GetKey(created);
            config.Collections.Add(created);
            List<AppInfo> join = collectionCreateJoin;
            bool joined = false;
            if (join != null)
            {
                for (int i = 0; i < join.Count; i++)
                {
                    AppInfo a = join[i];
                    if (a == null || string.IsNullOrEmpty(a.Key) || !CanJoinResourceCategory(a, root)) continue;
                    CollectionOps.AddMember(config, a.Key, key);
                    joined = true;
                }
            }
            if (!config.Save("CreateCollectionUnder"))
            {
                // 保存已整体回滚并提示过原因；刚写入的受管封面没有任何引用，可以安全清理
                if (draft.PastedCover != null) DeleteManagedCoverIfUnused(cover, null);
                collectionInfo.ShowEditNotice("没有保存成功，填的内容还在，可以再试一次。", true);
                return;
            }

            ExpandCollectionAncestors(key);
            if (parentKey.Length > 0) expandedCollections.Add(parentKey);
            if (draft.KeepCreating && !joined)
            {
                collectionInfo.ContinueCreating(name);   // 留在新建页接着建下一个
                RefreshGrid();
                collectionSidebar.ScrollToKey(key);
                return;
            }
            collectionCreateJoin = null;
            collectionInfo.EndEdit();
            if (!joined)
            {
                currentCollection = key;
                collectionEntered = false;
                revealActiveCollection = true;
                collectionSidebar.SetActive(key);
            }
            else revealActiveCollection = false;
            RefreshGrid();
            if (!joined) collectionSidebar.ScrollToKey(key);
        }

        /// <summary>
        /// 编辑集合落库：名称、位置、简介、封面在同一次保存里提交。改名和换位置按「先改名、再移动」
        /// 的顺序改内存并排队（数据库也按这个顺序应用），校验已保证两步都不会撞名。
        /// 保存失败时配置已整体回滚，编辑页保留已填内容可直接重试。
        /// </summary>
        private void CommitCollectionEdit(CollectionEditDraft draft)
        {
            ResourceCollection c = FindCollectionByKey(currentCollection);
            if (c == null)
            {
                collectionInfo.EndEdit();   // 集合已经不在了：关掉编辑页并按现状刷新
                RefreshGrid();
                return;
            }
            string name = (draft.Name ?? string.Empty).Trim();
            string parentKey = draft.ParentCollection ?? string.Empty;
            if (ValidateCollectionEditDraft(name, parentKey) != null)
            {
                collectionInfo.RefreshValidation();
                return;
            }
            string finalCover = ResolveDraftCover(draft);
            if (finalCover == null) return;

            string oldCover = c.Cover;
            string oldKey = CollectionKeys.GetKey(c);
            bool renamed = !string.Equals(name, c.Name, StringComparison.Ordinal);
            bool moved = !string.Equals(c.ParentCollection ?? string.Empty, parentKey, StringComparison.Ordinal);
            Dictionary<string, string> keyMap = new Dictionary<string, string>(StringComparer.Ordinal);   // 旧键 → 最终键
            try
            {
                string renamedKey = oldKey;
                if (renamed)
                {
                    keyMap = CollectionOps.RenameSubtree(config, c, name);
                    renamedKey = CollectionKeys.GetKey(c);
                }
                if (moved)
                {
                    CollectionMoveRequest request = new CollectionMoveRequest();
                    request.SourceKey = renamedKey;
                    request.TargetKey = parentKey;
                    request.Kind = parentKey.Length == 0 ? CollectionMoveKind.ToRoot : CollectionMoveKind.Into;
                    CollectionMovePlan plan;
                    string moveError;
                    if (!CollectionOps.TryBuildMovePlan(config, request, out plan, out moveError) || plan == null || plan.NoOp)
                        throw new InvalidOperationException(moveError ?? "目标位置无效。");
                    CollectionOps.ApplyMovePlan(config, plan);
                    keyMap = ComposeCollectionKeyMaps(keyMap, plan.KeyMap);
                }
                // 两步都成功后才给改名排队：中途失败时数据库的待办队列里不会留下半截操作
                if (renamed) config.QueueCategoryRename(oldKey, renamedKey);
            }
            catch (Exception ex)
            {
                // 与移动集合一致：不管哪种异常都不能把改了一半的内存留给下一次保存
                Paths.LogError(ex);
                config.ReloadFromDatabase();   // 内存里可能只改了一半：按数据库恢复
                if (draft.PastedCover != null) DeleteManagedCoverIfUnused(finalCover, null);
                RefreshGrid();
                collectionInfo.ShowEditNotice("没有保存：" + ex.Message, true);
                return;
            }

            c.Description = draft.Description ?? string.Empty;
            c.Cover = finalCover;
            c.CoverFocusX = ClampFocus(draft.FocusX);
            c.CoverFocusY = ClampFocus(draft.FocusY);
            if (!config.Save("OnCollectionEditSave"))
            {
                // 保存已整体回滚并提示过原因；刚写入的受管封面没有任何引用，可以安全清理
                if (draft.PastedCover != null) DeleteManagedCoverIfUnused(finalCover, null);
                collectionInfo.ShowEditNotice("没有保存成功，改的内容还在，可以再试一次。", true);
                return;
            }

            if (keyMap.Count > 0)
            {
                RemapExpandedCollections(keyMap);
                string mapped;
                if (currentCollection != null && keyMap.TryGetValue(currentCollection, out mapped)) currentCollection = mapped;
            }
            if (moved)
            {
                if (parentKey.Length > 0) expandedCollections.Add(parentKey);
                revealActiveCollection = true;
                pendingCollectionSidebarKeyMap = keyMap;
            }
            DeleteManagedCoverIfUnused(oldCover, finalCover);
            collectionInfo.EndEdit();
            collectionSidebar.SetActive(currentCollection ?? string.Empty);
            RefreshGrid();   // 侧边栏/信息页随名称、位置、简介与封面同步刷新
            if (moved && currentCollection != null) collectionSidebar.ScrollToKey(currentCollection);
        }

        /// <summary>把「改名」和「移动」两步的键映射合成一张（原始旧键 → 最终键）。</summary>
        private static Dictionary<string, string> ComposeCollectionKeyMaps(Dictionary<string, string> first,
            Dictionary<string, string> second)
        {
            if (first == null || first.Count == 0)
                return new Dictionary<string, string>(second, StringComparer.Ordinal);
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> entry in first)
            {
                string final;
                result[entry.Key] = second.TryGetValue(entry.Value, out final) ? final : entry.Value;
            }
            return result;
        }

        /// <summary>
        /// 编辑页点位置栏：弹出当前分类的集合树（逐级级联），选中即换位置。
        /// 编辑已有集合时，它自己和它的子集合不能选。
        /// </summary>
        private void OnCollectionPickLocation(Rectangle fieldScreen)
        {
            if (!collectionInfo.IsEditing) return;
            string selected = collectionInfo.EditParentKey;
            string blockedKey = collectionInfo.IsCreating ? null : currentCollection;
            CollectionIndex index = new CollectionIndex(config.Collections);
            List<MenuWindow.MenuItem> items = new List<MenuWindow.MenuItem>();
            MenuWindow.MenuItem top = MkMenu(currentCategory + "（最外层）",
                delegate { collectionInfo.SetEditLocation(string.Empty); });
            top.Checked = selected.Length == 0;
            items.Add(top);
            List<MenuWindow.MenuItem> tree = BuildLocationMenu(index, null, selected, blockedKey);
            if (tree.Count > 0)
            {
                items.Add(MkSep());
                items.AddRange(tree);
            }
            MenuWindow mw = new MenuWindow(items, 240);
            mw.Location = FixLocation(new Point(fieldScreen.Left, fieldScreen.Bottom + Dpi(4)), mw.Size);
            mw.Show(this);
        }

        /// <summary>位置菜单的一级：没有子集合的直接可选；有子集合的悬停展开下一级（展开时才构建）。</summary>
        private List<MenuWindow.MenuItem> BuildLocationMenu(CollectionIndex index, string parentKey,
            string selected, string blockedKey)
        {
            List<MenuWindow.MenuItem> result = new List<MenuWindow.MenuItem>();
            List<ResourceCollection> collections = CollectionChildrenForMenu(index, parentKey);
            for (int i = 0; i < collections.Count; i++)
            {
                ResourceCollection c = collections[i];
                string key = CollectionKeys.GetKey(c);
                if (blockedKey != null && CollectionKeys.IsDescendantOrSelf(blockedKey, key))
                {
                    MenuWindow.MenuItem self = MkMenu(c.Name + "（正在编辑）", null);
                    self.Disabled = true;
                    result.Add(self);
                    continue;
                }
                if (CollectionChildrenForMenu(index, key).Count == 0)
                {
                    MenuWindow.MenuItem leaf = MkMenu(c.Name, delegate { collectionInfo.SetEditLocation(key); });
                    leaf.Checked = string.Equals(key, selected, StringComparison.Ordinal);
                    result.Add(leaf);
                    continue;
                }
                MenuWindow.MenuItem node = MkMenu(c.Name, null);
                string name = c.Name;
                node.SubItemsFactory = delegate
                {
                    List<MenuWindow.MenuItem> sub = new List<MenuWindow.MenuItem>();
                    MenuWindow.MenuItem here = MkMenu("放在「" + name + "」里", delegate { collectionInfo.SetEditLocation(key); });
                    here.Checked = string.Equals(key, selected, StringComparison.Ordinal);
                    sub.Add(here);
                    sub.Add(MkSep());
                    sub.AddRange(BuildLocationMenu(index, key, selected, blockedKey));
                    return sub;
                };
                result.Add(node);
            }
            return result;
        }
    }
}
