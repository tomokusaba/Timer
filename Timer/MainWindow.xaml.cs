using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Timer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _uiTimer;
        private readonly Stopwatch _stopwatch = new();
        private readonly List<Rectangle> _cells = new(capacity: 160);
        private readonly List<int> _fragmentPattern = [];      // 断片化パターン（位置）
        private readonly List<int> _blockTypes = [];           // ブロックの種類
        private int[] _initialBlocks = [];                     // 初期状態（空き含む）

        private TimeSpan _targetDuration = TimeSpan.Zero;
        private TimeSpan _accumulated = TimeSpan.Zero;
        private bool _isRunning;

        private const int UnmovableRows = 2;                   // システム領域の行数（赤・移動不可）
        private const int FreeSpacePercent = 25;              // 空き領域の割合
        private const int FragmentedFilePercent = 40;         // 断片化ファイルの割合

        // デフラグの色
        private static readonly Brush CellStroke = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x80));
        private static readonly Brush CellSystemFill = Brushes.Red;              // システムファイル（移動不可）
        private static readonly Brush CellUnprocessedFill = new SolidColorBrush(Color.FromRgb(0x00, 0xC0, 0xC0)); // 未処理データ（連続）
        private static readonly Brush CellFragmentedFill = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0xFF));  // 断片化ファイル（青）
        private static readonly Brush CellProcessedFill = new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0x00));   // 最適化済み
        private static readonly Brush CellFreeFill = Brushes.White;              // 空き領域
        private static readonly Brush CellMovingFill = Brushes.Yellow;           // 移動中
        private static readonly Brush CellReadingFill = Brushes.Orange;          // 読み取り中

        public MainWindow()
        {
            InitializeComponent();

            _uiTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _uiTimer.Tick += (_, _) => UpdateUi();

            Loaded += (_, _) => InitializeDiskGrid();
        }

        private void InitializeDiskGrid()
        {
            _cells.Clear();
            DiskGrid.Children.Clear();

            for (var i = 0; i < DiskGrid.Rows * DiskGrid.Columns; i++)
            {
                var rect = new Rectangle
                {
                    Margin = new Thickness(0.5),
                    Fill = CellUnprocessedFill,
                    Stroke = CellStroke,
                    StrokeThickness = 0.5,
                    RadiusX = 0,
                    RadiusY = 0
                };

                _cells.Add(rect);
                DiskGrid.Children.Add(rect);
            }

            GenerateFragmentPattern();
            SetTarget(TimeSpan.FromMinutes(5));
            ResetInternal();
            UpdateUi(force: true);
        }

        private void GenerateFragmentPattern()
        {
            var rng = new Random();  // 毎回異なるシード
            var rows = DiskGrid.Rows;
            var cols = DiskGrid.Columns;
            var totalCells = rows * cols;
            var systemCells = UnmovableRows * cols;           // 先頭の赤（システム）
            var movableCells = totalCells - systemCells;      // 移動可能領域

            _fragmentPattern.Clear();
            _blockTypes.Clear();

            // 移動可能領域の各種類の数を計算
            var freeCount = movableCells * FreeSpacePercent / 100;           // 白
            var dataCount = movableCells - freeCount;                        // データ
            var fragmentedCount = dataCount * FragmentedFilePercent / 100;   // 青
            var continuousCount = dataCount - fragmentedCount;               // シアン

            // 移動可能領域のブロック: 0=空き, 1=連続, 2=断片化
            var movableBlocks = new int[movableCells];
            var idx = 0;
            
            for (var i = 0; i < fragmentedCount && idx < movableCells; i++, idx++)
                movableBlocks[idx] = 2;  // 断片化（青）
            for (var i = 0; i < continuousCount && idx < movableCells; i++, idx++)
                movableBlocks[idx] = 1;  // 連続（シアン）
            for (; idx < movableCells; idx++)
                movableBlocks[idx] = 0;  // 空き（白）

            // 激しくシャッフルして断片化（移動可能領域のみ）
            for (var round = 0; round < 5; round++)
            {
                for (var i = movableBlocks.Length - 1; i > 0; i--)
                {
                    var j = rng.Next(i + 1);
                    (movableBlocks[i], movableBlocks[j]) = (movableBlocks[j], movableBlocks[i]);
                }
            }

            // 初期状態を保存（システム領域 + 移動可能領域）
            _initialBlocks = new int[totalCells];
            for (var i = 0; i < systemCells; i++)
                _initialBlocks[i] = 3;  // システム（赤）
            for (var i = 0; i < movableCells; i++)
                _initialBlocks[systemCells + i] = movableBlocks[i];

            // データブロックの位置と種類を記録（前方から順に）
            // これにより、前方のデータから緑になり、空きが相対的に後ろに残る
            var dataPositions = new List<(int position, int type)>();
            for (var i = 0; i < movableCells; i++)
            {
                if (movableBlocks[i] == 1 || movableBlocks[i] == 2)  // データのみ
                {
                    dataPositions.Add((systemCells + i, movableBlocks[i]));
                }
            }

            // 前方から順に処理されるよう、位置でソート
            // （既に前から順に追加しているが明示的に）
            dataPositions.Sort((a, b) => a.position.CompareTo(b.position));

            foreach (var (position, type) in dataPositions)
            {
                _fragmentPattern.Add(position);
                _blockTypes.Add(type);
            }
        }

        private void StartPause_Click(object sender, RoutedEventArgs e)
        {
            ValidationText.Text = string.Empty;

            if (_isRunning)
            {
                Pause();
                return;
            }

            if (!TryReadTargetDuration(out var duration, out var error))
            {
                ValidationText.Text = error;
                return;
            }

            if (_targetDuration != duration)
            {
                SetTarget(duration);
                ResetInternal();
            }

            Start();
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            ValidationText.Text = string.Empty;

            if (!TryReadTargetDuration(out var duration, out var error))
            {
                ValidationText.Text = error;
                return;
            }

            SetTarget(duration);
            ResetInternal();
            UpdateUi(force: true);
        }

        private void Start()
        {
            if (_targetDuration <= TimeSpan.Zero)
            {
                return;
            }

            _isRunning = true;
            _stopwatch.Restart();
            _uiTimer.Start();

            StartPauseButton.Content = "一時停止";
            StatusText.Text = "ファイル システムを最適化中...";
        }

        private void Pause()
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _uiTimer.Stop();
            _stopwatch.Stop();
            _accumulated += _stopwatch.Elapsed;

            StartPauseButton.Content = "開始";
            StatusText.Text = "一時停止中";
        }

        private void ResetInternal()
        {
            _isRunning = false;
            _uiTimer.Stop();
            _stopwatch.Reset();
            _accumulated = TimeSpan.Zero;

            GenerateFragmentPattern();

            StartPauseButton.Content = "開始";
            StatusText.Text = "待機中";
        }

        private void SetTarget(TimeSpan duration)
        {
            _targetDuration = duration;
            TargetText.Text = $"目標: {FormatTime(duration)}";
        }

        private void UpdateUi(bool force = false)
        {
            if (_targetDuration <= TimeSpan.Zero)
            {
                RemainingText.Text = "00:00";
                Progress.Value = 0;
                PercentText.Text = "0%";
                ElapsedText.Text = "経過: 00:00";
                PaintCells(progress: 0, activityPhase: 0, isCompleted: false);
                return;
            }

            var elapsed = _accumulated + (_isRunning ? _stopwatch.Elapsed : TimeSpan.Zero);

            if (_isRunning && elapsed >= _targetDuration)
            {
                // Snap to exact completion at the target time.
                elapsed = _targetDuration;
                _accumulated = _targetDuration;
                _stopwatch.Reset();
                _isRunning = false;
                _uiTimer.Stop();

                StartPauseButton.Content = "開始";
                StatusText.Text = "完了";
            }

            var remaining = _targetDuration - elapsed;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            var progress01 = _targetDuration.TotalMilliseconds <= 0
                ? 0
                : Math.Clamp(elapsed.TotalMilliseconds / _targetDuration.TotalMilliseconds, 0, 1);

            RemainingText.Text = FormatTime(remaining);
            ElapsedText.Text = $"経過: {FormatTime(elapsed)}";

            var percent = (int)Math.Round(progress01 * 100, MidpointRounding.AwayFromZero);
            Progress.Value = percent;
            PercentText.Text = $"{percent}%";

            var phase = (int)(elapsed.TotalMilliseconds / 70);
            PaintCells(progress01, phase, isCompleted: progress01 >= 1.0);

            if (!force)
            {
                return;
            }
        }

        private void PaintCells(double progress, int activityPhase, bool isCompleted)
        {
            if (_cells.Count == 0 || _fragmentPattern.Count == 0)
            {
                return;
            }

            var cols = DiskGrid.Columns;
            var total = _cells.Count;
            var systemCells = UnmovableRows * cols;           // システム領域（赤・固定）
            var movableCells = total - systemCells;           // 移動可能領域

            if (movableCells <= 0)
            {
                return;
            }

            // データと空きの数
            var dataCount = _fragmentPattern.Count;
            var freeCount = movableCells - dataCount;

            // 進捗に応じた処理済み数
            var optimizedCount = (int)Math.Floor(progress * dataCount);
            optimizedCount = Math.Clamp(optimizedCount, 0, dataCount);

            // 進捗に応じて後ろに移動する空きの数
            var movedFreeCount = (int)Math.Floor(progress * freeCount);
            movedFreeCount = Math.Clamp(movedFreeCount, 0, freeCount);

            // 1) システム領域（先頭数行）: 常に赤（移動不可）
            for (var i = 0; i < systemCells && i < total; i++)
            {
                _cells[i].Fill = CellSystemFill;
            }

            // 2) 初期状態から空きの位置を前方から収集
            var freePositions = new List<int>();
            for (var i = 0; i < movableCells; i++)
            {
                var globalIdx = systemCells + i;
                if ((uint)globalIdx < (uint)_initialBlocks.Length && _initialBlocks[globalIdx] == 0)
                {
                    freePositions.Add(i);
                }
            }

            // 3) 移動可能領域の状態を構築
            var movableState = new int[movableCells];  // 0=空き, 1=連続, 2=断片化, 9=緑
            for (var i = 0; i < movableCells; i++)
            {
                var globalIdx = systemCells + i;
                if ((uint)globalIdx < (uint)_initialBlocks.Length)
                {
                    movableState[i] = _initialBlocks[globalIdx];
                }
            }

            // 4) 処理済みデータを緑(9)に
            for (var i = 0; i < optimizedCount; i++)
            {
                var globalPos = _fragmentPattern[i];
                var localPos = globalPos - systemCells;
                if ((uint)localPos < (uint)movableCells)
                {
                    movableState[localPos] = 9;  // 緑
                }
            }

            // 5) 前方の空きを後方に移動（進捗に応じて）
            //    前方のN個の空きを、後方の非空き位置と交換
            var swapped = 0;
            var backIdx = movableCells - 1;
            
            for (var freeIdx = 0; freeIdx < freePositions.Count && swapped < movedFreeCount; freeIdx++)
            {
                var frontFreePos = freePositions[freeIdx];
                
                // この空きがまだ前方にあるか確認（後ろの方にあるなら交換不要）
                if (frontFreePos >= movableCells - movedFreeCount)
                {
                    continue;  // 既に後方にある
                }

                // 後方から非空きの位置を探す
                while (backIdx > frontFreePos && movableState[backIdx] == 0)
                {
                    backIdx--;
                }

                if (backIdx <= frontFreePos)
                {
                    break;
                }

                // 交換
                (movableState[frontFreePos], movableState[backIdx]) = (movableState[backIdx], movableState[frontFreePos]);
                backIdx--;
                swapped++;
            }

            // 6) 状態をBrushに変換
            var movableResult = new Brush[movableCells];
            for (var i = 0; i < movableCells; i++)
            {
                movableResult[i] = movableState[i] switch
                {
                    9 => CellProcessedFill,    // 緑（処理済み）
                    2 => CellFragmentedFill,   // 断片化（青）
                    1 => CellUnprocessedFill,  // 連続（シアン）
                    _ => CellFreeFill          // 空き（白）
                };
            }

            // 7) アニメーション: 移動中・読み取り中のブロックを表示（1箇所ずつ）
            var remainingData = dataCount - optimizedCount;
            if (!isCompleted && remainingData > 0 && _isRunning)
            {
                // 未処理データの位置を収集
                var unprocessedPositions = new List<int>();
                for (var i = 0; i < movableCells; i++)
                {
                    if (movableState[i] == 1 || movableState[i] == 2)
                    {
                        unprocessedPositions.Add(i);
                    }
                }

                // アニメーションフェーズ: 読み取り→移動→書き込みのサイクル
                var cyclePhase = activityPhase % 3;

                if (unprocessedPositions.Count > 0)
                {
                    // 現在処理中のデータブロック（1箇所だけ）
                    var currentIdx = (activityPhase / 3) % unprocessedPositions.Count;
                    var currentPos = unprocessedPositions[currentIdx];

                    if (cyclePhase == 0)
                    {
                        // 読み取り中（オレンジ）
                        movableResult[currentPos] = CellReadingFill;
                    }
                    else if (cyclePhase == 1)
                    {
                        // 移動中（黄色）
                        movableResult[currentPos] = CellMovingFill;
                    }
                    // cyclePhase == 2: 書き込み完了、元の色
                }
            }

            // 8) 移動可能領域をセルに反映
            for (var i = 0; i < movableCells; i++)
            {
                var globalIndex = systemCells + i;
                if ((uint)globalIndex < (uint)total)
                {
                    _cells[globalIndex].Fill = movableResult[i];
                }
            }
        }

        private bool TryReadTargetDuration(out TimeSpan duration, out string error)
        {
            duration = TimeSpan.Zero;
            error = string.Empty;

            if (!TryParseNonNegativeInt(MinutesBox.Text, out var minutes))
            {
                error = "分は 0 以上の整数で入力してください。";
                return false;
            }

            if (!TryParseNonNegativeInt(SecondsBox.Text, out var seconds))
            {
                error = "秒は 0 以上の整数で入力してください。";
                return false;
            }

            if (seconds >= 60)
            {
                error = "秒は 0〜59 の範囲で入力してください。";
                return false;
            }

            if (minutes == 0 && seconds == 0)
            {
                error = "1 秒以上を指定してください。";
                return false;
            }

            duration = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
            return true;
        }

        private static bool TryParseNonNegativeInt(string? text, out int value)
        {
            if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value >= 0;
            }

            value = 0;
            return false;
        }

        private static string FormatTime(TimeSpan time)
        {
            if (time.TotalHours >= 1)
            {
                return time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
            }

            return time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        }
    }
}