﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls.Dialogs;
using Warp.Headers;
using Warp.Tools;

namespace Warp.Controls.TaskDialogs.TwoD
{
    /// <summary>
    /// Interaction logic for DialogParticleExport.xaml
    /// </summary>
    public partial class Dialog2DParticleExport : UserControl
    {
        public Movie[] Movies;
        public string ImportPath, ExportPath;
        public Options Options;
        public event Action Close;

        string InputSuffix = "";
        string InputFolder = "";

        List<UIElement> DisableWhileProcessing;

        bool IsCanceled = false;

        List<float> Timings = new List<float>();

        public Dialog2DParticleExport(Movie[] movies, string importPath, string exportPath, Options options)
        {
            InitializeComponent();

            Options = options;

            if (movies.Any(m => m.OptionsCTF != null))
            {
                Options.Tasks.InputPixelSize = movies.First(m => m.OptionsCTF != null).OptionsCTF.BinnedPixelSizeMean;
            }
            else
            {
                Options.Tasks.Export2DPreflip = false;
                CheckPreflip.IsEnabled = false;
            }

            Movies = movies;
            ImportPath = importPath;
            ExportPath = exportPath;

            DataContext = Options;

            if (!Options.Tasks.Export2DDoAverages && !Options.Tasks.Export2DDoMovies)
                Options.Tasks.Export2DDoAverages = true;

            DisableWhileProcessing = new List<UIElement>
            {
                CheckOneInputPerItem,
                SliderImportAngPix,
                SliderExportAngPix,
                TextSuffix,
                SliderBoxSize,
                SliderParticleDiameter,
                RadioAverage,
                RadioStack,
                RadioStar,
                CheckInvert,
                CheckNormalize,
                CheckPreflip,
                CheckRelative,
                CheckFilter,
                CheckManual
            };

            #region Check if using different input files for each item makes sense

            bool FoundMatchingPrefix = false;
            string ImportName = Helper.PathToName(importPath);
            foreach (var item in Movies)
            {
                if (ImportName.Contains(item.RootName))
                {
                    FoundMatchingPrefix = true;
                    InputSuffix = ImportName.Substring(item.RootName.Length);
                    break;
                }
            }

            if (FoundMatchingPrefix)
            {
                CheckOneInputPerItem.IsEnabled = true;
                CheckOneInputPerItem.ToolTip = $"{InputSuffix} will be used as suffix.";
                Options.Tasks.InputOnePerItem = true;

                FileInfo Info = new FileInfo(importPath);
                InputFolder = Info.DirectoryName;
                if (InputFolder.Last() != '/' && InputFolder.Last() != '\\')
                    InputFolder += "/";
            }
            else
            {
                CheckOneInputPerItem.IsEnabled = false;
                CheckOneInputPerItem.ToolTip = "No matching suffix found.";
                Options.Tasks.InputOnePerItem = false;
            }

            #endregion

            Options.Tasks.OutputSuffix = Options.Tasks.InputOnePerItem ? InputSuffix : "_" + Helper.PathToName(importPath);
        }

        private void ButtonCancel_OnClick(object sender, RoutedEventArgs e)
        {
            DataContext = null;
            Close?.Invoke();
        }

        private async void ButtonExport_OnClick(object sender, RoutedEventArgs e)
        {
            bool DoAverage = (bool)RadioAverage.IsChecked;
            bool DoStack = (bool)RadioStack.IsChecked;
            bool DoOnlyStar = (bool)RadioStar.IsChecked;

            bool Invert = (bool)CheckInvert.IsChecked;
            bool Normalize = (bool)CheckNormalize.IsChecked;
            bool Preflip = (bool)CheckPreflip.IsChecked;

            float AngPix = (float)Options.Tasks.InputPixelSize;

            bool Relative = (bool)CheckRelative.IsChecked;

            bool Filter = (bool)CheckFilter.IsChecked;
            bool Manual = (bool)CheckManual.IsChecked;

            int BoxSize = (int)Options.Tasks.Export2DBoxSize;
            int NormDiameter = (int)Options.Tasks.Export2DParticleDiameter;

            ProgressWrite.Visibility = Visibility.Visible;
            ProgressWrite.IsIndeterminate = true;
            PanelButtons.Visibility = Visibility.Collapsed;
            PanelRemaining.Visibility = Visibility.Visible;

            foreach (var element in DisableWhileProcessing)
                element.IsEnabled = false;

            await Task.Run(async () =>
            {
                #region Get all movies that can potentially be used

                List<Movie> ValidMovies = Movies.Where(v =>
                {
                    if (!Filter && v.UnselectFilter && v.UnselectManual == null)
                        return false;
                    if (!Manual && v.UnselectManual != null && (bool)v.UnselectManual)
                        return false;
                    if (v.OptionsCTF == null)
                        return false;
                    return true;
                }).ToList();
                List<string> ValidMovieNames = ValidMovies.Select(m => m.RootName).ToList();

                if (ValidMovies.Count == 0)
                    await Dispatcher.Invoke(async () =>
                    {
                        await ((MainWindow)Application.Current.MainWindow).ShowMessageAsync("Oopsie",
                                                                                            "No items were found to extract particles from.\n" +
                                                                                            "Please make sure the names match, estimate the CTF for all items, and review your filtering thresholds.");
                    });

                #endregion

                #region Read table and intersect its micrograph set with valid movies

                Star TableIn;

                if (Options.Tasks.InputOnePerItem)
                {
                    List<Star> Tables = new List<Star>();
                    foreach (var item in Movies)
                    {
                        string StarPath = InputFolder + item.RootName + InputSuffix + ".star";
                        if (File.Exists(StarPath))
                        {
                            Star TableItem = new Star(StarPath);
                            if (!TableItem.HasColumn("rlnMicrographName"))
                                TableItem.AddColumn("rlnMicrographName", item.Name);

                            if (item.PickingThresholds.ContainsKey(InputSuffix) && TableItem.HasColumn("rlnAutopickFigureOfMerit"))
                            {
                                float Threshold = (float)item.PickingThresholds[InputSuffix];
                                float[] Scores = TableItem.GetColumn("rlnAutopickFigureOfMerit").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                                int[] InvalidRows = Helper.ArrayOfSequence(0, TableItem.RowCount, 1).Where(i => Scores[i] < Threshold).ToArray();
                                TableItem.RemoveRows(InvalidRows);
                            }

                            Tables.Add(TableItem);
                        }
                    }

                    TableIn = new Star(Tables.ToArray());
                }
                else
                {
                    TableIn = new Star(ImportPath);
                }

                if (!TableIn.HasColumn("rlnMicrographName"))
                    throw new Exception("Couldn't find rlnMicrographName column.");
                if (!TableIn.HasColumn("rlnCoordinateX"))
                    throw new Exception("Couldn't find rlnCoordinateX column.");
                if (!TableIn.HasColumn("rlnCoordinateY"))
                    throw new Exception("Couldn't find rlnCoordinateY column.");

                Dictionary<string, List<int>> Groups = new Dictionary<string, List<int>>();
                {
                    string[] ColumnMicNames = TableIn.GetColumn("rlnMicrographName");
                    for (int r = 0; r < ColumnMicNames.Length; r++)
                    {
                        if (!Groups.ContainsKey(ColumnMicNames[r]))
                            Groups.Add(ColumnMicNames[r], new List<int>());
                        Groups[ColumnMicNames[r]].Add(r);
                    }
                    Groups = Groups.ToDictionary(group => Helper.PathToName(group.Key), group => group.Value);

                    Groups = Groups.Where(group => ValidMovieNames.Contains(group.Key)).ToDictionary(group => group.Key, group => group.Value);
                }

                bool[] RowsIncluded = new bool[TableIn.RowCount];
                foreach (var group in Groups)
                    foreach (var r in group.Value)
                        RowsIncluded[r] = true;
                List<int> RowsNotIncluded = new List<int>();
                for (int r = 0; r < RowsIncluded.Length; r++)
                    if (!RowsIncluded[r])
                        RowsNotIncluded.Add(r);

                ValidMovies = ValidMovies.Where(v => Groups.ContainsKey(v.RootName)).ToList();

                if (ValidMovies.Count == 0)     // Exit if there is nothing to export, otherwise errors will be thrown below
                    return;

                #endregion

                #region Make sure all columns are there

                if (!TableIn.HasColumn("rlnMagnification"))
                    TableIn.AddColumn("rlnMagnification", "10000.0");
                else
                    TableIn.SetColumn("rlnMagnification", Helper.ArrayOfConstant("10000.0", TableIn.RowCount));

                if (!TableIn.HasColumn("rlnDetectorPixelSize"))
                    TableIn.AddColumn("rlnDetectorPixelSize", Options.GetProcessingParticleExport().BinnedPixelSizeMean.ToString("F5", CultureInfo.InvariantCulture));
                else
                    TableIn.SetColumn("rlnDetectorPixelSize", Helper.ArrayOfConstant(Options.GetProcessingParticleExport().BinnedPixelSizeMean.ToString("F5", CultureInfo.InvariantCulture), TableIn.RowCount));

                if (!TableIn.HasColumn("rlnVoltage"))
                    TableIn.AddColumn("rlnVoltage", "300.0");

                if (!TableIn.HasColumn("rlnSphericalAberration"))
                    TableIn.AddColumn("rlnSphericalAberration", "2.7");

                if (!TableIn.HasColumn("rlnAmplitudeContrast"))
                    TableIn.AddColumn("rlnAmplitudeContrast", "0.07");

                if (!TableIn.HasColumn("rlnPhaseShift"))
                    TableIn.AddColumn("rlnPhaseShift", "0.0");

                if (!TableIn.HasColumn("rlnDefocusU"))
                    TableIn.AddColumn("rlnDefocusU", "0.0");

                if (!TableIn.HasColumn("rlnDefocusV"))
                    TableIn.AddColumn("rlnDefocusV", "0.0");

                if (!TableIn.HasColumn("rlnDefocusAngle"))
                    TableIn.AddColumn("rlnDefocusAngle", "0.0");

                if (!TableIn.HasColumn("rlnCtfMaxResolution"))
                    TableIn.AddColumn("rlnCtfMaxResolution", "999.0");

                if (!TableIn.HasColumn("rlnImageName"))
                    TableIn.AddColumn("rlnImageName", "None");

                if (!TableIn.HasColumn("rlnMicrographName"))
                    TableIn.AddColumn("rlnMicrographName", "None");

                #endregion

                int MaxDevices = 999;
                int UsedDevices = Math.Min(MaxDevices, GPU.GetDeviceCount());

                if (IsCanceled)
                    return;

                #region Load gain reference if needed

                Image[] ImageGain = new Image[UsedDevices];
                if (!string.IsNullOrEmpty(Options.Import.GainPath) && Options.Import.CorrectGain && File.Exists(Options.Import.GainPath))
                    for (int d = 0; d < UsedDevices; d++)
                    {
                        GPU.SetDevice(d);
                        ImageGain[d] = MainWindow.LoadAndPrepareGainReference();
                    }

                #endregion

                bool Overwrite = true;
                if (DoAverage || DoStack)
                    foreach (var movie in ValidMovies)
                    {
                        bool FileExists = File.Exists((DoAverage ? movie.ParticlesDir : movie.ParticleMoviesDir) + movie.RootName + Options.Tasks.OutputSuffix + ".mrcs");
                        if (FileExists)
                        {
                            await Dispatcher.Invoke(async () =>
                            {
                                var DialogResult = await ((MainWindow)Application.Current.MainWindow).ShowMessageAsync("Some particle files already exist. Overwrite them?",
                                                                                                                       "",
                                                                                                                       MessageDialogStyle.AffirmativeAndNegative,
                                                                                                                       new MetroDialogSettings()
                                                                                                                       {
                                                                                                                           AffirmativeButtonText = "Yes",
                                                                                                                           NegativeButtonText = "No"
                                                                                                                       });
                                if (DialogResult == MessageDialogResult.Negative)
                                    Overwrite = false;
                            });
                            break;
                        }
                    }

                Star TableOut = null;
                {
                    Dictionary<string, Star> MicrographTables = new Dictionary<string, Star>();

                    #region Get coordinates

                    float[] PosX = TableIn.GetColumn("rlnCoordinateX").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    float[] PosY = TableIn.GetColumn("rlnCoordinateY").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                    float[] ShiftX = TableIn.HasColumn("rlnOriginX") ? TableIn.GetColumn("rlnOriginX").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray() : new float[TableIn.RowCount];
                    float[] ShiftY = TableIn.HasColumn("rlnOriginY") ? TableIn.GetColumn("rlnOriginY").Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray() : new float[TableIn.RowCount];
                    for (int r = 0; r < TableIn.RowCount; r++)
                    {
                        PosX[r] -= ShiftX[r];
                        PosY[r] -= ShiftY[r];
                    }

                    if (TableIn.HasColumn("rlnOriginX"))
                        TableIn.RemoveColumn("rlnOriginX");
                    if (TableIn.HasColumn("rlnOriginY"))
                        TableIn.RemoveColumn("rlnOriginY");

                    #endregion

                    Dispatcher.Invoke(() => ProgressWrite.MaxValue = ValidMovies.Count);

                    Helper.ForEachGPU(ValidMovies, (movie, gpuID) =>
                    {
                        if (IsCanceled)
                            return;

                        Stopwatch ItemTime = new Stopwatch();
                        ItemTime.Start();

                        MapHeader OriginalHeader = MapHeader.ReadFromFile(movie.Path);

                        #region Set up export options

                        ProcessingOptionsParticlesExport ExportOptions = Options.GetProcessingParticleExport();
                        ExportOptions.DoAverage = DoAverage;
                        ExportOptions.DoStack = DoStack;
                        ExportOptions.Invert = Invert;
                        ExportOptions.Normalize = Normalize;
                        ExportOptions.PreflipPhases = Preflip;
                        ExportOptions.Dimensions = OriginalHeader.Dimensions.MultXY((float)Options.PixelSizeMean);
                        ExportOptions.BoxSize = BoxSize;
                        ExportOptions.Diameter = NormDiameter;

                        #endregion

                        bool FileExists = File.Exists((DoAverage ? movie.ParticlesDir : movie.ParticleMoviesDir) + movie.RootName + ExportOptions.Suffix + ".mrcs");

                        #region Load and prepare original movie

                        Image OriginalStack = null;
                        decimal ScaleFactor = 1M / (decimal)Math.Pow(2, (double)ExportOptions.BinTimes);

                        if (!DoOnlyStar && (!FileExists || Overwrite))
                            MainWindow.LoadAndPrepareHeaderAndMap(movie.Path, ImageGain[gpuID], ScaleFactor, out OriginalHeader, out OriginalStack);

                        if (IsCanceled)
                        {
                            foreach (Image gain in ImageGain)
                                gain?.Dispose();
                            OriginalStack?.Dispose();

                            return;
                        }

                        #endregion

                        #region Figure out relative or absolute path to particle stack

                        string PathStack = (ExportOptions.DoStack ? movie.ParticleMoviesDir : movie.ParticlesDir) + movie.RootName + ExportOptions.Suffix + ".mrcs";
                        string PathMicrograph = movie.Path;
                        if (Relative)
                        {
                            Uri UriStar = new Uri(ExportPath);
                            PathStack = UriStar.MakeRelativeUri(new Uri(PathStack)).ToString();
                            PathMicrograph = UriStar.MakeRelativeUri(new Uri(PathMicrograph)).ToString();
                        }

                        #endregion

                        #region Update row values

                        List<int> GroupRows = Groups[movie.RootName];
                        List<float2> Positions = new List<float2>();

                        float Astigmatism = (float)movie.CTF.DefocusDelta / 2;
                        float PhaseShift = movie.OptionsCTF.DoPhase ? movie.GridCTFPhase.GetInterpolated(new float3(0.5f)) * 180 : 0;
                        int ImageNameIndex = TableIn.GetColumnID("rlnImageName");

                        foreach (var r in GroupRows)
                        {
                            float3 Position = new float3(PosX[r] * AngPix / ExportOptions.Dimensions.X,
                                                         PosY[r] * AngPix / ExportOptions.Dimensions.Y,
                                                         0.5f);
                            float LocalDefocus = movie.GridCTF.GetInterpolated(Position);

                            TableIn.SetRowValue(r, "rlnDefocusU", ((LocalDefocus + Astigmatism) * 1e4f).ToString("F1", CultureInfo.InvariantCulture));
                            TableIn.SetRowValue(r, "rlnDefocusV", ((LocalDefocus - Astigmatism) * 1e4f).ToString("F1", CultureInfo.InvariantCulture));
                            TableIn.SetRowValue(r, "rlnDefocusAngle", movie.CTF.DefocusAngle.ToString("F1", CultureInfo.InvariantCulture));

                            TableIn.SetRowValue(r, "rlnVoltage", movie.CTF.Voltage.ToString("F1", CultureInfo.InvariantCulture));
                            TableIn.SetRowValue(r, "rlnSphericalAberration", movie.CTF.Cs.ToString("F4", CultureInfo.InvariantCulture));
                            TableIn.SetRowValue(r, "rlnAmplitudeContrast", movie.CTF.Amplitude.ToString("F3", CultureInfo.InvariantCulture));
                            TableIn.SetRowValue(r, "rlnPhaseShift", PhaseShift.ToString("F1", CultureInfo.InvariantCulture));
                            TableIn.SetRowValue(r, "rlnCtfMaxResolution", movie.CTFResolutionEstimate.ToString("F1", CultureInfo.InvariantCulture));

                            TableIn.SetRowValue(r, "rlnCoordinateX", (PosX[r] * AngPix / (float)ExportOptions.BinnedPixelSizeMean).ToString("F2", CultureInfo.InvariantCulture));
                            TableIn.SetRowValue(r, "rlnCoordinateY", (PosY[r] * AngPix / (float)ExportOptions.BinnedPixelSizeMean).ToString("F2", CultureInfo.InvariantCulture));

                            TableIn.SetRowValue(r, "rlnImageName", PathStack);

                            TableIn.SetRowValue(r, "rlnMicrographName", PathMicrograph);

                            Positions.Add(new float2(PosX[r] * AngPix,
                                                     PosY[r] * AngPix));
                        }

                        #endregion

                        #region Populate micrograph table with rows for all exported particles

                        Star MicrographTable = new Star(TableIn.GetColumnNames());

                        int StackDepth = (ExportOptions.DoAverage || DoOnlyStar)
                                             ? 1
                                             : (OriginalHeader.Dimensions.Z - ExportOptions.SkipFirstN - ExportOptions.SkipLastN) /
                                               ExportOptions.StackGroupSize;

                        int pi = 0;
                        for (int i = 0; i < StackDepth; i++)
                        {
                            foreach (var r in GroupRows)
                            {
                                List<string> Row = TableIn.GetRow(r).ToList();
                                Row[ImageNameIndex] = (++pi).ToString("D7") + "@" + Row[ImageNameIndex];
                                MicrographTable.AddRow(Row);
                            }
                        }

                        #endregion

                        #region Finally, process and export the actual particles

                        if (!DoOnlyStar && (!FileExists || Overwrite))
                        {
                            movie.ExportParticles(OriginalStack, Positions.ToArray(), ExportOptions);
                            OriginalStack.Dispose();
                        }

                        #endregion

                        #region Add this micrograph's table to global collection, update remaining time estimate

                        lock (MicrographTables)
                        {
                            MicrographTables.Add(movie.RootName, MicrographTable);

                            Timings.Add(ItemTime.ElapsedMilliseconds / (float)UsedDevices);

                            int MsRemaining = (int)(MathHelper.Mean(Timings) * (ValidMovies.Count - MicrographTables.Count));
                            TimeSpan SpanRemaining = new TimeSpan(0, 0, 0, 0, MsRemaining);

                            Dispatcher.Invoke(() => TextRemaining.Text = SpanRemaining.ToString((int)SpanRemaining.TotalHours > 0 ? @"hh\:mm\:ss" : @"mm\:ss"));

                            Dispatcher.Invoke(() =>
                            {
                                ProgressWrite.IsIndeterminate = false;
                                ProgressWrite.Value = MicrographTables.Count;
                            });
                        }

                        #endregion

                    }, 1);

                    if (MicrographTables.Count > 0)
                        TableOut = new Star(MicrographTables.Values.ToArray());
                }
                
                foreach (Image gain in ImageGain)
                    gain?.Dispose();

                if (IsCanceled)
                    return;

                TableOut.Save(ExportPath);
            });

            DataContext = null;
            Close?.Invoke();
        }

        private void ButtonAbort_OnClick(object sender, RoutedEventArgs e)
        {
            ButtonAbort.IsEnabled = false;
            IsCanceled = true;
        }
    }
}
