using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AppManager.Controls;

namespace AppManager
{
    /// <summary>
    /// 桌面收纳设置与清单操作。
    /// </summary>
    public partial class MainForm
    {
        private DesktopBackupPanel desktopBackupPanel;

        private DesktopBackupPanel CreateDesktopBackupPanel()
        {
            DesktopBackupPanel panel = new DesktopBackupPanel();
            panel.RefreshRequested += RefreshDesktopBackupPanel;
            panel.OptionsChanged += SaveDesktopOptions;
            panel.OrganizeRequested += DoOrganize;
            panel.UpdateRequested += DoUpdateBackup;
            panel.RestoreRequested += RestoreDesktopEntry;
            panel.RemoveRequested += RemoveDesktopEntry;
            panel.RestoreAllRequested += RestoreAllDesktopEntries;
            return panel;
        }

        private void SaveDesktopOptions(DesktopOrganizeOptions options, bool automatic)
        {
            if (!DesktopWorkBlocked(desktopBackupPanel) && (options.Shortcuts || options.Files || options.Folders))
            {
                DesktopOrganizeOptions previous = new DesktopOrganizeOptions(config);
                bool oldAuto = config.AutoOrganizeDesktop;
                options.ApplyTo(config, automatic);
                if (!config.Save("desktop.options")) previous.ApplyTo(config, oldAuto);
            }
            desktopBackupPanel.Bind(config);
        }

        /// <summary>把当前配置里的清单交给面板（面板显示出来、配置重新加载或操作结束后调用）。</summary>
        private void RefreshDesktopBackupPanel()
        {
            if (desktopBackupPanel == null || desktopBackupPanel.IsDisposed) return;
            // 后台正在整理或还原时，清单正被工作线程修改：这时不去读它，操作结束后会再刷新一次
            if (desktopOperationRunning || pendingAutoStash != 0) return;
            desktopBackupPanel.SetItems(new List<DesktopBackupEntry>(config.DesktopBackup));
        }

        /// <summary>
        /// 整理桌面、还原、自动收纳等正在后台搬动文件时，清单里的操作先不做，在面板上方说明原因。
        /// 否则同一份清单会被两个后台操作同时处理（同一条被还原两次、报「还原失败」）。
        /// </summary>
        private bool DesktopWorkBlocked(DesktopBackupPanel panel)
        {
            if (!DesktopWorkRunning()) return false;
            panel.SetStatus("整理桌面或还原正在进行，完成后再操作清单。", true);
            if (!settingsOpen) ShowNotice("桌面收纳正在进行，请稍后再试。");
            return true;
        }

        /// <summary>开始一次清单操作：与「整理桌面」菜单的操作互斥（共用 desktopOperationRunning），期间菜单按钮停用、面板忙碌。</summary>
        private bool BeginDesktopPanelWork(DesktopBackupPanel panel)
        {
            if (DesktopWorkBlocked(panel)) return false;
            desktopOperationRunning = true;
            organizeBtn.Enabled = false;
            panel.SetBusy(true);
            return true;
        }

        private void EndDesktopPanelWork(DesktopBackupPanel panel)
        {
            desktopOperationRunning = false;
            organizeBtn.Enabled = true;
            panel.SetBusy(false);
        }

        /// <summary>还原一条：确认后在后台把它移回桌面并从清单里去掉；失败时保留清单项并说明原因。</summary>
        private void RestoreDesktopEntry(DesktopBackupEntry e)
        {
            DesktopBackupPanel panel = desktopBackupPanel;
            if (panel == null || panel.Busy || DesktopWorkBlocked(panel) || !config.DesktopBackup.Contains(e)) return;
            if (!ConfirmDialog.Show("还原条目", "还原「" + Path.GetFileName(e.CurrentPath) + "」？\n" +
                (e.IsCopy ? "原件相同时仅将备份副本放入回收站；原件缺失则移回，内容不同时保留副本。" : "会把收纳的条目移回桌面。"), "还原", "取消")) return;
            if (!BeginDesktopPanelWork(panel)) return;   // 确认框开着时可能有别的桌面操作开始了：再查一次
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
                    EndDesktopPanelWork(panel);
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
                        panel.SetStatus("已处理「" + Path.GetFileName(e.CurrentPath) + "」的还原记录。", false);
                });
            });
        }

        /// <summary>移除一条：只删清单记录，不动备份文件。</summary>
        private void RemoveDesktopEntry(DesktopBackupEntry e)
        {
            DesktopBackupPanel panel = desktopBackupPanel;
            if (panel == null || panel.Busy || DesktopWorkBlocked(panel) || !config.DesktopBackup.Contains(e)) return;
            if (!ConfirmDialog.Show("移除清单项", "从清单移除这条记录？\n不会删除备份文件。", "移除", "取消")) return;
            if (!BeginDesktopPanelWork(panel)) return;   // 确认框开着时可能有别的桌面操作开始了：再查一次
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
                    EndDesktopPanelWork(panel);
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
            if (panel == null || panel.Busy || DesktopWorkBlocked(panel) || config.DesktopBackup.Count == 0) return;
            int total = config.DesktopBackup.Count;
            if (!ConfirmDialog.Show("全部还原", "还原清单中的 " + total + " 项？\n搬家项移回桌面；副本项核对原件后清理，原件缺失则移回。内容不同时保留副本和记录。", "全部还原", "取消")) return;
            if (!BeginDesktopPanelWork(panel)) return;   // 确认框开着时可能有别的桌面操作开始了：再查一次
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
                    EndDesktopPanelWork(panel);
                    ReportBatchSaveFailure(cfg, saveError);
                    ReloadLibrary();
                    RefreshDesktopBackupPanel();
                    if (failure != null)
                        MessageDialog.Show(this, "还原失败：" + failure.Message, "全部还原", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    else if (saveError == null)
                        panel.SetStatus("已处理 " + n + " 项。" + (n < total ? "未还原的记录已保留，可重试。" : ""), n < total);
                });
            });
        }
    }
}
