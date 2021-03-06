﻿using ProcessGuard.Common;
using ProcessGuard.Common.Models;
using ProcessGuard.Common.Utility;
using MahApps.Metro.Controls;
using MahApps.Metro.Controls.Dialogs;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.ServiceProcess;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;

namespace ProcessGuard
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow
    {
        private MainWindowViewModel _mainWindowViewModel;

        /// <summary>
        /// 用于定时检查服务状态的Timer
        /// </summary>
        private Timer _checkTimer;

        public MainWindow()
        {
            InitializeComponent();
            _mainWindowViewModel = new MainWindowViewModel() { ConfigItems = new ObservableCollection<ConfigItem>(), StatusColor = "Transparent" };
            UpdateServiceStatus();
            DataContext = _mainWindowViewModel;
            this.MetroDialogOptions.AnimateShow = true;
            this.MetroDialogOptions.AnimateHide = false;
            _mainWindowViewModel.ConfigItems = ConfigHelper.LoadConfigFile();
            _mainWindowViewModel.IsRun = _mainWindowViewModel.IsRun == true ? false : true;
            _checkTimer = new Timer();
            _checkTimer.Elapsed += CheckTimer_Elapsed;
            _checkTimer.Start();
            _checkTimer.Interval = 1000;
        }

        #region 窗体事件

        /// <summary>
        /// 按钮点击事件
        /// </summary>
        private async void Button_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;

            switch (button?.Name)
            {
                case nameof(btnAdd):
                    var dialog = new CustomDialog(this.MetroDialogOptions) { Content = this.Resources["CustomAddDialog"], Title = string.Empty };

                    await this.ShowMetroDialogAsync(dialog);
                    await dialog.WaitUntilUnloadedAsync();
                    break;

                case nameof(btnMinus):
                    ConfigItem selectedItem = null;

                    if (configDataGrid.SelectedCells?.Count > 0)
                    {
                        selectedItem = configDataGrid.SelectedCells[0].Item as ConfigItem;
                    }

                    if (selectedItem == null)
                    {
                        break;
                    }

                    MessageDialogResult result = await ShowMessageDialogAsync("注意", "确认删除选中的内容吗？");

                    if (result == MessageDialogResult.Affirmative)
                        _mainWindowViewModel.ConfigItems.Remove(selectedItem);

                    break;

                case "btnSelectFile":
                    var filePath = Utils.GetFileNameByFileDialog();
                    if (!string.IsNullOrEmpty(filePath))
                    {
                        FileInfo fileInfo = new FileInfo(filePath);
                        _mainWindowViewModel.SelectedFile = fileInfo.FullName;
                        _mainWindowViewModel.SeletedProcessName = fileInfo.Name.Replace(fileInfo.Extension, string.Empty);
                    }
                    break;

                case nameof(btnSave):
                    SaveChanges();
                    break;

                case nameof(btnUndo):
                    UndoChanges();
                    break;

                case nameof(btnStart):
                    StartService();
                    break;

                case nameof(btnStop):
                    StopService();
                    break;

                case nameof(btnUninstall):
                    await UninstallService();
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// 停止服务
        /// </summary>
        private void StopService()
        {
            ServiceController service = null;
            try
            {
                service = new ServiceController(Constants.PROCESS_GUARD_SERVICE);
                service.Stop();
            }
            catch (Exception)
            {
                // do nothing
            }
            finally
            {
                service.Dispose();
            }
        }

        /// <summary>
        /// 卸载服务
        /// </summary>
        /// <returns>卸载过程中是否产生错误</returns>
        private async Task<bool> UninstallService()
        {
            var error = string.Empty;

            MessageDialogResult dialogResult = await ShowMessageDialogAsync("注意", "确认卸载守护服务吗？");

            if (dialogResult == MessageDialogResult.Affirmative)
            {
                CreateServiceFile();
                string cmd = @"%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe /U ";
                cmd += $"/LogFile={ConfigHelper.GetAppDataFilePath("myLog.InstallLog")} ";
                cmd += ConfigHelper.GetAppDataFilePath(Constants.FILE_GUARD_SERVICE);

                await Task.Run(() =>
                {
                    ApplicationLoader.RunCmdAndGetOutput(cmd, out var _, out error);
                });
            }

            return !string.IsNullOrEmpty(error);
        }

        /// <summary>
        /// 若服务已存在，启动服务，否则先安装，再启动服务
        /// </summary>
        private async void StartService()
        {
            if (GetServiceStatus(Constants.PROCESS_GUARD_SERVICE) == default(ServiceControllerStatus))
            {
                CreateServiceFile();

                string cmd = @"%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe ";
                cmd += $"/LogFile={ConfigHelper.GetAppDataFilePath("myLog.InstallLog")} ";
                cmd += $@"{ConfigHelper.GetAppDataFilePath(Constants.FILE_GUARD_SERVICE)}
                             Net Start ProcessGuardService
                             sc config ProcessGuardService = auto";

                await Task.Run(() =>
                {
                    ApplicationLoader.RunCmdAndGetOutput(cmd, out var _, out var _);
                });
            }
            else
            {
                ServiceController service = null;
                try
                {
                    service = new ServiceController(Constants.PROCESS_GUARD_SERVICE);
                    service.Start();
                }
                catch (Exception)
                {
                    // do nothing
                }
                finally
                {
                    service.Dispose();
                }
            }
        }

        /// <summary>
        /// 在安装和卸载服务之前，需要确保服务文件已存在
        /// </summary>
        private void CreateServiceFile()
        {
            var serviceFileName = ConfigHelper.GetAppDataFilePath(Constants.FILE_GUARD_SERVICE);

            // 从资源文件中拷贝出服务程序
            if (!File.Exists(serviceFileName))
            {
                File.WriteAllBytes(serviceFileName, Properties.Resources.ProcessGuardService);
            }
        }

        /// <summary>
        /// 和CLosing事件配套使用的变量，防止因为异步引起的代码未执行完毕
        /// </summary>
        bool _confirmClose = false;

        /// <summary>
        /// 主窗体关闭时事件
        /// </summary>
        private async void MetroWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_confirmClose && ConfigHelper.LoadConfigFile().Serialize() != _mainWindowViewModel.ConfigItems.Serialize())
            {
                e.Cancel = true;
                MessageDialogResult dialogResult = await ShowMessageDialogAsync("注意", "当前配置项有改动，是否放弃所有更改？");
                if (dialogResult == MessageDialogResult.Affirmative)
                {
                    _confirmClose = true;
                    this.Close();
                }
            }
        }

        /// <summary>
        /// 用于检查服务状态的计时器
        /// </summary>
        private void CheckTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            UpdateServiceStatus();
        }

        #endregion

        #region 窗体私有函数

        /// <summary>
        /// 撤销本次的所有改动
        /// </summary>
        private async void UndoChanges()
        {
            MessageDialogResult result = await ShowMessageDialogAsync("注意", "确认撤销本次的所有改动吗？");

            if (result == MessageDialogResult.Affirmative)
                this._mainWindowViewModel.ConfigItems = ConfigHelper.LoadConfigFile();
        }

        /// <summary>
        /// 保存当前配置项
        /// </summary>
        private async void SaveChanges()
        {
            ConfigHelper.SaveConfigs(_mainWindowViewModel.ConfigItems);

            MessageDialogResult result = await ShowMessageDialogAsync("注意", "保存配置文件成功，将在重启服务后生效，是否立即重启服务？");

            if (result == MessageDialogResult.Affirmative)
            {
                ConfigHelper.SaveConfigs(_mainWindowViewModel.ConfigItems);
                StopService();
                // 停止后立即开始可能会启动失败，这里延迟2秒
                await Task.Delay(2000);
                StartService();
            }
        }

        /// <summary>
        /// 获取指定Windows系统服务的状态，如果其没有安装，默认返回0
        /// </summary>
        /// <param name="serviceName">服务名</param>
        /// <returns></returns>
        private ServiceControllerStatus GetServiceStatus(string serviceName)
        {
            ServiceController service = null;
            try
            {
                service = new ServiceController(Constants.PROCESS_GUARD_SERVICE);
                return service.Status;
            }
            catch (Exception)
            {
                return default(ServiceControllerStatus);
            }
            finally
            {
                service.Dispose();
            }
        }

        /// <summary>
        /// 更新当前的服务状态标识
        /// </summary>
        private void UpdateServiceStatus()
        {
            string statusColor = "";
            string runStatus = "";
            bool? isRun = null;

            switch (GetServiceStatus(Constants.PROCESS_GUARD_SERVICE))
            {
                case ServiceControllerStatus.ContinuePending:
                    statusColor = "Yellow";
                    runStatus = "即将继续";
                    isRun = false;
                    break;
                case ServiceControllerStatus.Paused:
                    statusColor = "Orange";
                    runStatus = "已暂停";
                    isRun = false;
                    break;
                case ServiceControllerStatus.PausePending:
                    statusColor = "Yellow";
                    runStatus = "即将暂停";
                    isRun = true;
                    break;
                case ServiceControllerStatus.Running:
                    statusColor = "Green";
                    runStatus = "运行中";
                    isRun = true;
                    break;
                case ServiceControllerStatus.StartPending:
                    statusColor = "Yellow";
                    runStatus = "正在启动";
                    isRun = false;
                    break;
                case ServiceControllerStatus.Stopped:
                    statusColor = "Orange";
                    runStatus = "已停止";
                    isRun = false;
                    break;
                case ServiceControllerStatus.StopPending:
                    statusColor = "Yellow";
                    runStatus = "正在停止";
                    isRun = true;
                    break;
                default:
                    statusColor = "Red";
                    runStatus = "未安装";
                    isRun = null;
                    break;
            }

            if (!string.IsNullOrEmpty(runStatus))
            {
                this.Dispatcher.Invoke(() =>
                {
                    _mainWindowViewModel.StatusColor = statusColor;
                    _mainWindowViewModel.RunStatus = runStatus;
                    _mainWindowViewModel.IsRun = isRun;
                });
            }
        }

        /// <summary>
        /// 使用自定义的标题和消息弹出确认对话框
        /// </summary>
        /// <param name="title">标题</param>
        /// <param name="message">要提示的消息</param>
        /// <returns></returns>
        private async Task<MessageDialogResult> ShowMessageDialogAsync(string title, string message)
        {
            var mySettings = new MetroDialogSettings()
            {
                AffirmativeButtonText = "是的",
                NegativeButtonText = "取消",
                ColorScheme = MetroDialogOptions.ColorScheme,
                DialogButtonFontSize = 20D,
                AnimateShow = false,
                AnimateHide = false,
            };

            MessageDialogResult result = await this.ShowMessageAsync(title, message,
                                                                     MessageDialogStyle.AffirmativeAndNegative, mySettings);

            return result;
        }

        /// <summary>
        /// 关闭当前弹出的Dialog窗口
        /// </summary>
        private async void CloseCustomDialog(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var dialog = button.TryFindParent<BaseMetroDialog>();

            var mySettings = new MetroDialogSettings()
            {
                AffirmativeButtonText = "是的",
                NegativeButtonText = "取消",
                ColorScheme = MetroDialogOptions.ColorScheme,
                DialogButtonFontSize = 20D,
                AnimateShow = false,
                AnimateHide = false,
            };

            switch (button.Name)
            {
                case "btnConfirmAdd":
                    if (_mainWindowViewModel[nameof(_mainWindowViewModel.SelectedFile)] == null &&
                        _mainWindowViewModel[nameof(_mainWindowViewModel.SeletedProcessName)] == null)
                    {
                        _mainWindowViewModel.ConfigItems.Add(new ConfigItem()
                        {
                            EXEFullPath = _mainWindowViewModel.SelectedFile,
                            ProcessName = _mainWindowViewModel.SeletedProcessName,
                            OnlyOpenOnce = _mainWindowViewModel.IsOnlyOpenOnce,
                            Minimize = _mainWindowViewModel.IsMinimize,
                        });

                        await this.HideMetroDialogAsync(dialog);
                    }
                    break;

                case "btnCancelAdd":
                    await this.HideMetroDialogAsync(dialog);
                    break;
                default:
                    break;
            }
        }

        #endregion

    }
}
