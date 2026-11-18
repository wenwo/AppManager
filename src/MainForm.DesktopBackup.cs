using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 桌面备份清单：嵌在设置页右侧（左侧目录「桌面备份清单」），不另弹窗口。
    /// 面板只展示清单并发出请求；这里负责确认、在后台还原或移除、保存配置，以及把结果写回面板。
    /// </summary>
    public partial class MainForm
    {
        private DesktopBackupPanel desktopBackupPanel;

        private DesktopBackupPanel CreateDesktopBackupPanel()
        {
            DesktopBackupPanel panel = new DesktopBackupPanel();
            panel.RefreshRequested += RefreshDesktopBackupPanel;
            panel.RestoreRequested += RestoreDesktopEntry;
            panel.RemoveRequested += RemoveDesktopEntry;
            panel.RestoreAllRequested += RestoreAllDesktopEntries;
            return panel;
        }

        /// <summary>把当前配置里的清单交给面板（面板显示出来、配置重新加载或操作结束后调用）。</summary>
        private void RefreshDesktopBackupPanel()
        {
            if (desktopBackupPanel == null || desktopBackupPanel.IsDisposed) return;
            desktopBackupPanel.SetItems(new List<DesktopBackupEntry>(config.DesktopBackup));
        }

        /// <summary>还原一条：确认后在后台把它移回桌面并从清单里去掉；失败时保留清单项并说明原因。</summary>
        private void RestoreDesktopEntry(DesktopBackupEntry e)
        {
            DesktopBackupPanel panel = desktopBackupPanel;
            if (panel == null || panel.Busy || !config.DesktopBackup.Contains(e)) return;
            if (!ConfirmDialog.Show("还原条目", "把「" + Path.GetFileName(e.CurrentPath) + "」还原到桌面？", "还原", "取消")) return;
            panel.SetBusy(true);
            AppConfig cfg = config;
            DesktopOrganizer worker = organizer;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool restored = false;
                string error = null;
                string restoreError = null;
                Exception failure = null;
                string saveError = null;
                AppConfig.BeginSaveBatch();
                try
                {
                    restored = worker.RestoreOne(e);
                    if (!restored) restoreError = worker.LastRestoreError;
                    if (restored && !worker.RemoveEntry(e, out error))
                        error = "文件已还原，但清单记录未能保存：" + (error ?? cfg.DatabaseError);
                }
                catch (Exception ex) { failure = ex; Paths.LogError(ex); }
                finally { saveError = AppConfig.EndSaveBatch(); }
                RunOnUi(delegate
                {
                    panel.SetBusy(false);
                    // 保存失败统一回滚并只提示一次（附带本操作的具体说明），不再另弹第二个框。
                    if (saveError != null) ReportBatchSaveFailure(cfg, error ?? saveError);
                    ReloadLibrary();
                    RefreshDesktopBackupPanel();
                    if (failure != null)
                        MessageDialog.Show(this, "还原失败：" + failure.Message, "还原", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    else if (!restored)
                        MessageDialog.Show(this, "还原失败：" + (restoreError ?? "未知原因") + "\n已保留该清单项。", "还原",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else if (error != null && saveError == null)
                        MessageDialog.Show(this, error, "还原", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else if (saveError == null)
                        panel.SetStatus("已把「" + Path.GetFileName(e.CurrentPath) + "」还原到桌面。", false);
                });
            });
        }

        /// <summary>移除一条：只删清单记录，不动备份文件。</summary>
        private void RemoveDesktopEntry(DesktopBackupEntry e)
        {
            DesktopBackupPanel panel = desktopBackupPanel;
            if (panel == null || panel.Busy || !config.DesktopBackup.Contains(e)) return;
            if (!ConfirmDialog.Show("移除清单项", "从清单移除这条记录？\n不会删除备份文件。", "移除", "取消")) return;
            panel.SetBusy(true);
            AppConfig cfg = config;
            DesktopOrganizer worker = organizer;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                Exception failure = null;
                string saveError = null;
                AppConfig.BeginSaveBatch();
                try { worker.RemoveEntry(e, out error); }
                catch (Exception ex) { failure = ex; Paths.LogError(ex); }
                finally { saveError = AppConfig.EndSaveBatch(); }
                RunOnUi(delegate
                {
                    panel.SetBusy(false);
                    if (saveError != null) ReportBatchSaveFailure(cfg, "清单项未能移除：" + (error ?? saveError));
                    RefreshDesktopBackupPanel();
                    if (failure != null)
                        MessageDialog.Show(this, "移除失败：" + failure.Message, "移除清单项", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    else if (error != null && saveError == null)
                        MessageDialog.Show(this, "移除失败：" + error, "移除清单项", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    else if (saveError == null)
                        panel.SetStatus("已从清单移除「" + Path.GetFileName(e.CurrentPath) + "」。", false);
                });
            });
        }

        /// <summary>全部还原：确认后在后台把清单里的条目逐个移回桌面。</summary>
        private void RestoreAllDesktopEntries()
        {
            DesktopBackupPanel panel = desktopBackupPanel;
            if (panel == null || panel.Busy || config.DesktopBackup.Count == 0) return;
            if (!ConfirmDialog.Show("全部还原", "把清单中的 " + config.DesktopBackup.Count + " 个条目全部还原到桌面？", "全部还原", "取消")) return;
            panel.SetBusy(true);
            AppConfig cfg = config;
            DesktopOrganizer worker = organizer;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int n = 0;
                Exception failure = null;
                string saveError = null;
                AppConfig.BeginSaveBatch();
                try { n = worker.RestoreAll(); }
                catch (Exception ex) { failure = ex; Paths.LogError(ex); }
                finally { saveError = AppConfig.EndSaveBatch(); }
                RunOnUi(delegate
                {
                    panel.SetBusy(false);
                    ReportBatchSaveFailure(cfg, saveError);
                    ReloadLibrary();
                    RefreshDesktopBackupPanel();
                    if (failure != null)
                        MessageDialog.Show(this, "还原失败：" + failure.Message, "全部还原", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    else if (saveError == null)
                        panel.SetStatus("已还原 " + n + " 个条目到桌面。", false);   // 成功只在面板顶上说一声，不弹窗
                });
            });
        }
    }
}
