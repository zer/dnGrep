using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using dnGREP.Common;
using NLog;
using SevenZip;

namespace dnGREP.Engines.Archive
{
    public class GrepEngineArchive : GrepEngineBase, IGrepEngine
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();

        public GrepEngineArchive() : base() { }

        public GrepEngineArchive(GrepEngineInitParams param)
            : base(param)
        { }

        public bool IsSearchOnly
        {
            get { return true; }
        }

        public List<GrepSearchResult> Search(string file, string searchPattern, SearchType searchType, GrepSearchOption searchOptions, Encoding encoding)
        {
            using (FileStream fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
            {
                return Search(fileStream, file, searchPattern, searchType, searchOptions, encoding);
            }
        }

        public List<GrepSearchResult> Search(Stream input, string file, string searchPattern, SearchType searchType, GrepSearchOption searchOptions, Encoding encoding)
        {
            List<GrepSearchResult> searchResults = new List<GrepSearchResult>();

            var includeRegexPatterns = new List<Regex>();
            var excludeRegexPatterns = new List<Regex>();
            Utils.PrepareFilters(FileFilter, includeRegexPatterns, excludeRegexPatterns);

            List<string> hiddenDirectories = new List<string>();

            try
            {
                using (SevenZipExtractor extractor = new SevenZipExtractor(input))
                {
                    foreach (var fileInfo in extractor.ArchiveFileData)
                    {
                        var attr = (FileAttributes)fileInfo.Attributes;
                        string innerFileName = fileInfo.FileName;

                        if (fileInfo.IsDirectory)
                        {
                            if (!FileFilter.IncludeHidden && attr.HasFlag(FileAttributes.Hidden) && !hiddenDirectories.Contains(innerFileName))
                                hiddenDirectories.Add(innerFileName);

                            continue;
                        }

                        if (CheckHidden(FileFilter, attr) &&
                            CheckHidden(FileFilter, innerFileName, hiddenDirectories) &&
                            CheckSize(FileFilter, fileInfo.Size) &&
                            CheckDate(FileFilter, fileInfo) &&
                            IsPatternMatch(innerFileName, includeRegexPatterns) &&
                            !IsPatternMatch(innerFileName, excludeRegexPatterns))
                        {
                            using (Stream stream = new MemoryStream())
                            {
                                extractor.ExtractFile(innerFileName, stream);
                                stream.Seek(0, SeekOrigin.Begin);

                                if (CheckBinary(FileFilter, stream))
                                {
                                    IGrepEngine engine = GrepEngineFactory.GetSearchEngine(innerFileName, initParams, FileFilter);
                                    var innerFileResults = engine.Search(stream, innerFileName, searchPattern, searchType, searchOptions, encoding);

                                    if (innerFileResults.Count > 0)
                                    {
                                        using (Stream readStream = new MemoryStream())
                                        {
                                            extractor.ExtractFile(innerFileName, readStream);
                                            readStream.Seek(0, SeekOrigin.Begin);
                                            using (StreamReader streamReader = new StreamReader(readStream))
                                            {
                                                foreach (var result in innerFileResults)
                                                {
                                                    if (Utils.CancelSearch)
                                                        break;

                                                    if (!result.HasSearchResults)
                                                        result.SearchResults = Utils.GetLinesEx(streamReader, result.Matches, initParams.LinesBefore, initParams.LinesAfter);
                                                }
                                            }
                                            searchResults.AddRange(innerFileResults);
                                        }
                                    }
                                }
                                if (Utils.CancelSearch)
                                    break;
                            }
                        }
                    }
                }

                foreach (GrepSearchResult result in searchResults)
                {
                    result.FileNameDisplayed = file + "\\" + result.FileNameDisplayed;
                    result.FileNameReal = file;
                    result.ReadOnly = true;
                }
            }
            catch (Exception ex)
            {
                logger.Log<Exception>(LogLevel.Error, string.Format("Failed to search inside archive '{0}'", file), ex);
            }
            return searchResults;
        }

        private bool IsPatternMatch(string fileName, List<Regex> patterns)
        {
            bool isMatch = false;
            foreach (var pattern in patterns)
            {
                if (pattern.IsMatch(fileName) || Utils.CheckShebang(fileName, pattern.ToString()))
                {
                    isMatch = true;
                    break;
                }
            }
            return isMatch;
        }

        private bool CheckDate(FileFilter filter, ArchiveFileInfo fileInfo)
        {
            if (filter.DateFilter != FileDateFilter.None)
            {
                DateTime fileDate = filter.DateFilter == FileDateFilter.Created ? fileInfo.CreationTime : fileInfo.LastWriteTime;
                if (filter.StartTime.HasValue && fileDate < filter.StartTime.Value)
                {
                    return false;
                }
                if (filter.EndTime.HasValue && fileDate >= filter.EndTime.Value)
                {
                    return false;
                }
            }
            return true;
        }

        private bool CheckSize(FileFilter filter, ulong length)
        {
            if (filter.SizeFrom > 0 || filter.SizeTo > 0)
            {
                ulong sizeKB = length / 1000;
                if (filter.SizeFrom > 0 && sizeKB < (ulong)filter.SizeFrom)
                {
                    return false;
                }
                if (filter.SizeTo > 0 && sizeKB > (ulong)filter.SizeTo)
                {
                    return false;
                }
            }
            return true;
        }

        private bool CheckBinary(FileFilter filter, Stream stream)
        {
            if (!filter.IncludeBinary)
            {
                if (Utils.IsBinary(stream))
                    return false;
                else
                    stream.Seek(0, SeekOrigin.Begin);
            }
            return true;
        }

        private bool CheckHidden(FileFilter filter, FileAttributes attributes)
        {
            if (!filter.IncludeHidden && attributes.HasFlag(FileAttributes.Hidden))
                return false;

            return true;
        }

        private bool CheckHidden(FileFilter filter, string fileName, List<string> hiddenDirectories)
        {
            if (!filter.IncludeHidden && hiddenDirectories.Count > 0)
            {
                string path = Path.GetDirectoryName(fileName);
                foreach (var dir in hiddenDirectories)
                {
                    if (path.Contains(dir))
                        return false;
                }
            }
            return true;
        }

        public void Unload()
        {
            //Do nothing
        }

        public bool Replace(string sourceFile, string destinationFile, string searchPattern, string replacePattern, SearchType searchType, GrepSearchOption searchOptions, Encoding encoding)
        {
            throw new Exception("The method or operation is not supported.");
        }

        public Version FrameworkVersion
        {
            get { return Assembly.GetAssembly(typeof(IGrepEngine)).GetName().Version; }
        }

        public override void OpenFile(OpenFileArgs args)
        {
            string tempFolder = Path.Combine(Utils.GetTempFolder(), "dnGREP-Archive", Utils.GetHash(args.SearchResult.FileNameReal));
            string innerFileName = args.SearchResult.FileNameDisplayed.Substring(args.SearchResult.FileNameReal.Length).TrimStart(Path.DirectorySeparatorChar);
            string filePath = Path.Combine(tempFolder, innerFileName);

            if (!File.Exists(filePath))
            {
                // use the directory name to also include folders within the archive
                string directory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                using (SevenZipExtractor extractor = new SevenZipExtractor(args.SearchResult.FileNameReal))
                {
                    if (extractor.ArchiveFileData.Where(r => r.FileName == innerFileName && !r.IsDirectory).Any())
                    {
                        using (FileStream stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            try
                            {
                                extractor.ExtractFile(innerFileName, stream);
                            }
                            catch
                            {
                                args.UseBaseEngine = true;
                            }
                        }
                    }
                }
            }

            if (Utils.IsPdfFile(filePath) || Utils.IsWordFile(filePath))
                args.UseCustomEditor = false;

            GrepSearchResult newResult = new GrepSearchResult();
            newResult.FileNameReal = args.SearchResult.FileNameReal;
            newResult.FileNameDisplayed = args.SearchResult.FileNameDisplayed;
            OpenFileArgs newArgs = new OpenFileArgs(newResult, args.Pattern, args.LineNumber, args.UseCustomEditor, args.CustomEditor, args.CustomEditorArgs);
            newArgs.SearchResult.FileNameDisplayed = filePath;
            Utils.OpenFile(newArgs);
        }
    }
}
