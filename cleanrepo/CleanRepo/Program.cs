﻿using CleanRepo.Extensions;
using CommandLine;
using Microsoft.Build.Construction;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CleanRepo;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1304:Specify CultureInfo", Justification = "Annoying")]
static class Program
{
    static void Main(string[] args)
    {
#if DEBUG
        // TODO: Consider using launchSettings.json with command line args instead:
        //
        //   {
        //     "profiles": {
        //       "CleanRepo": {
        //         "commandName": "Project",
        //         "commandLineArgs": "--orphaned-images\r\n--base-path=\"/dotnet\""
        //       }
        //     }
        //   }
        //
        // ... to avoid hardcoded values in DEBUG preprocessor directives like this:
        args = new[] { "--orphaned-snippets", "--snippets-directory=c:\\users\\gewarren\\docs\\docs\\orleans" };
        //args = new[] { "--orphaned-snippets", "--relative-links", "--remove-hops", "--replace-redirects", "--orphaned-includes", "--orphaned-articles", "--orphaned-images",
        //"--articles-directory=c:\\users\\gewarren\\docs\\docs\\fundamentals", "--media-directory=c:\\users\\gewarren\\docs\\docs\\core",
        //"--includes-directory=c:\\users\\gewarren\\docs\\includes", "--snippets-directory=c:\\users\\gewarren\\docs\\samples\\snippets\\csharp\\vs_snippets_clr",
        //"--docfx-directory=c:\\users\\gewarren\\docs", "--url-base-path=/dotnet", "--delete=false"};
#endif

        Parser.Default.ParseArguments<Options>(args).WithParsed(RunOptions);
    }

    static void RunOptions(Options options)
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        string startDirectory = null;

        if (!String.IsNullOrEmpty(options.DocFxDirectory))
            startDirectory = options.DocFxDirectory;
        else if (!String.IsNullOrEmpty(options.ArticlesDirectory))
            startDirectory = options.ArticlesDirectory;
        else if (!String.IsNullOrEmpty(options.MediaDirectory))
            startDirectory = options.MediaDirectory;
        else if (!String.IsNullOrEmpty(options.SnippetsDirectory))
            startDirectory = options.SnippetsDirectory;
        else if (!String.IsNullOrEmpty(options.IncludesDirectory))
            startDirectory = options.IncludesDirectory;
        else
        {
            Console.WriteLine("\nEnter the path to the directory that contains the docfx.json file for the docset:\n");
            options.DocFxDirectory = Console.ReadLine();
            startDirectory = options.DocFxDirectory;
        }

        if (String.IsNullOrEmpty(startDirectory) || !Directory.Exists(startDirectory))
        {
            Console.WriteLine($"\nThe {startDirectory} directory doesn't exist.");
            return;
        }

        // Initialize the DocFxRepo object for all options.
        var docFxRepo = new DocFxRepo(startDirectory);

        // Determine if we're to delete orphans (or just report them).
        if (options.FindOrphanedImages
            || options.FindOrphanedSnippets
            || options.FindOrphanedIncludes
            || options.FindOrphanedArticles)
        {
            if (options.Delete is null)
            {
                options.Delete = false;
                Console.WriteLine("\nDo you want to delete orphans (y or n)?");
                var info = Console.ReadKey();
                if (info.KeyChar == 'y' || info.KeyChar == 'Y')
                {
                    options.Delete = true;
                }
            }
        }

        // Find orphaned articles.
        if (options.FindOrphanedArticles)
        {
            if (String.IsNullOrEmpty(options.ArticlesDirectory))
            {
                Console.WriteLine("\nEnter the path to the directory that you want to check for orphaned articles:\n");
                options.ArticlesDirectory = Console.ReadLine();
            }
            if (String.IsNullOrEmpty(options.ArticlesDirectory) || !Directory.Exists(options.ArticlesDirectory))
            {
                Console.WriteLine($"\nThe {options.ArticlesDirectory} directory doesn't exist.");
                return;
            }

            // Make sure the searchable directory is part of the same DocFx docset.
            if (!options.ArticlesDirectory.IsSubdirectoryOf(docFxRepo.DocFxDirectory.FullName))
            {
                Console.WriteLine($"'{options.ArticlesDirectory}' is not a child of the docfx.json file's directory '{docFxRepo.DocFxDirectory}'.");
                return;
            }

            Console.WriteLine($"\nSearching the '{options.ArticlesDirectory}' directory and its subdirectories for orphaned articles...");

            List<FileInfo> markdownFiles = GetMarkdownFiles(options.ArticlesDirectory, "snippets");

            if (docFxRepo.AllTocFiles is null || markdownFiles is null)
            {
                return;
            }

            ListOrphanedTopics(docFxRepo.AllTocFiles, markdownFiles, options.Delete.Value);
        }

        // Find orphaned images
        if (options.FindOrphanedImages)
        {
            if (String.IsNullOrEmpty(options.MediaDirectory))
            {
                Console.WriteLine("\nEnter the path to the directory that you want to check for orphaned media files:\n");
                options.MediaDirectory = Console.ReadLine();
            }
            if (String.IsNullOrEmpty(options.MediaDirectory) || !Directory.Exists(options.MediaDirectory))
            {
                Console.WriteLine($"\nThe {options.MediaDirectory} directory doesn't exist.");
                return;
            }

            // Make sure the searchable directory is part of the same DocFx docset.
            if (!options.MediaDirectory.IsSubdirectoryOf(docFxRepo.DocFxDirectory.FullName))
            {
                Console.WriteLine($"'{options.MediaDirectory}' is not a child of the docfx.json file's directory '{docFxRepo.DocFxDirectory}'.");
                return;
            }

            if (String.IsNullOrEmpty(options.UrlBasePath))
            {
                Console.WriteLine("\nEnter the URL base path for this docset, for example, '/dotnet' or '/windows/uwp':\n");
                options.UrlBasePath = Console.ReadLine();
            }

            docFxRepo.UrlBasePath = options.UrlBasePath;

            // Add regex to find image refs similar to 'social_image_url: "/dotnet/media/logo.png"'
            // This is done here (dynamically) because it relies on knowing the base path URL.
            docFxRepo.ImageLinkRegExes.Add($"social_image_url: ?\"?({docFxRepo.UrlBasePath}.*?(\\.(png|jpg|gif|svg))+)");

            // Gather media file names.
            docFxRepo.ImageRefs = GetMediaFiles(options.MediaDirectory);

            Console.WriteLine($"\nSearching the '{options.MediaDirectory}' directory recursively for orphaned .png/.jpg/.gif/.svg files...\n");

            docFxRepo.ListOrphanedImages(options.Delete.Value, "snippets");
        }

        // Catalog images
        if (options.CatalogImages)
        {
            if (String.IsNullOrEmpty(options.MediaDirectory))
            {
                Console.WriteLine("\nEnter the path to the directory where you want to catalog media files:\n");
                options.MediaDirectory = Console.ReadLine();
            }
            if (String.IsNullOrEmpty(options.MediaDirectory) || !Directory.Exists(options.MediaDirectory))
            {
                Console.WriteLine($"\nThe {options.MediaDirectory} directory doesn't exist.");
                return;
            }

            // Make sure the searchable directory is part of the same DocFx docset.
            if (!options.MediaDirectory.IsSubdirectoryOf(docFxRepo.DocFxDirectory.FullName))
            {
                Console.WriteLine($"'{options.MediaDirectory}' is not a child of the docfx.json file's directory '{docFxRepo.DocFxDirectory}'.");
                return;
            }

            if (String.IsNullOrEmpty(options.UrlBasePath))
            {
                Console.WriteLine("\nEnter the URL base path for this docset, for example, '/dotnet' or '/windows/uwp':\n");
                options.UrlBasePath = Console.ReadLine();
            }

            docFxRepo.UrlBasePath = options.UrlBasePath;

            // Add regex to find image refs similar to 'social_image_url: "/dotnet/media/logo.png"'
            // This is done here (dynamically) because it relies on knowing the base path URL.
            docFxRepo.ImageLinkRegExes.Add($"social_image_url: ?\"?({docFxRepo.UrlBasePath}.*?(\\.(png|jpg|gif|svg))+)");

            // Gather media file names.
            docFxRepo.ImageRefs = GetMediaFiles(options.MediaDirectory);

            Console.WriteLine($"\nCataloging the images in the '{options.MediaDirectory}' directory...\n");

            docFxRepo.OutputImageReferences();
        }

        // Find orphaned include-type files
        if (options.FindOrphanedIncludes)
        {
            if (String.IsNullOrEmpty(options.IncludesDirectory))
            {
                Console.WriteLine("\nEnter the path to the directory that you want to check for orphaned include files:\n");
                options.IncludesDirectory = Console.ReadLine();
            }
            if (String.IsNullOrEmpty(options.IncludesDirectory) || !Directory.Exists(options.IncludesDirectory))
            {
                Console.WriteLine($"\nThe {options.IncludesDirectory} directory doesn't exist.");
                return;
            }

            // Make sure the searchable directory is part of the same DocFx docset.
            if (!options.IncludesDirectory.IsSubdirectoryOf(docFxRepo.DocFxDirectory.FullName))
            {
                Console.WriteLine($"'{options.IncludesDirectory}' is not a child of the docfx.json file's directory '{docFxRepo.DocFxDirectory}'.");
                return;
            }

            Console.WriteLine($"\nSearching the '{options.IncludesDirectory}' directory recursively for orphaned .md files " +
                $"in directories named 'includes' or '_shared'.");

            Dictionary<string, int> includeFiles = GetIncludeFiles(options.IncludesDirectory);

            if (includeFiles.Count == 0)
            {
                Console.WriteLine("\nNo .md files were found in any directory named 'includes' or '_shared'.");
                return;
            }

            ListOrphanedIncludes(options.IncludesDirectory, includeFiles, options.Delete.Value);
        }

        // Find orphaned .cs and .vb files
        if (options.FindOrphanedSnippets)
        {
            if (String.IsNullOrEmpty(options.SnippetsDirectory))
            {
                Console.WriteLine("\nEnter the path to the directory that you want to check for orphaned snippet files:\n");
                options.SnippetsDirectory = Console.ReadLine();
            }
            if (String.IsNullOrEmpty(options.SnippetsDirectory) || !Directory.Exists(options.SnippetsDirectory))
            {
                Console.WriteLine($"\nThe {options.SnippetsDirectory} directory doesn't exist.");
                return;
            }

            // Make sure the searchable directory is part of the same DocFx docset.
            if (!options.SnippetsDirectory.IsSubdirectoryOf(docFxRepo.DocFxDirectory.FullName))
            {
                Console.WriteLine($"'{options.SnippetsDirectory}' is not a child of the docfx.json file's directory '{docFxRepo.DocFxDirectory}'.");
                return;
            }

            Console.WriteLine($"\nSearching the '{options.SnippetsDirectory}' directory recursively for orphaned .cs and .vb files.");

            // Get all .cs and .vb files.
            List<string> snippetFiles = GetSnippetFiles(options.SnippetsDirectory);
            if (snippetFiles.Count == 0)
            {
                Console.WriteLine("\nNo .cs or .vb files were found.");
                return;
            }

            // Catalog all the solution files and the project (directories) they reference.
            List<(string, List<string>)> solutionFiles = GetSolutionFiles(options.SnippetsDirectory);

            ListOrphanedSnippets(options.SnippetsDirectory, snippetFiles, solutionFiles, options.Delete.Value);
        }

        // Replace links to articles that are redirected in the master redirection files.
        if (options.ReplaceRedirectTargets)
        {
            // Get the directory that represents the docset.
            if (String.IsNullOrEmpty(options.ArticlesDirectory))
            {
                Console.WriteLine("\nEnter the path to the directory that contains the articles with links to fix up:\n");
                options.ArticlesDirectory = Console.ReadLine();
            }
            if (String.IsNullOrEmpty(options.ArticlesDirectory) || !Directory.Exists(options.ArticlesDirectory))
            {
                Console.WriteLine($"\nThe {options.ArticlesDirectory} directory doesn't exist.");
                return;
            }

            // Make sure the searchable directory is part of the same DocFx docset.
            if (!options.ArticlesDirectory.IsSubdirectoryOf(docFxRepo.DocFxDirectory.FullName))
            {
                Console.WriteLine($"'{options.ArticlesDirectory}' is not a child of the docfx.json file's directory '{docFxRepo.DocFxDirectory}'.");
                return;
            }

            if (String.IsNullOrEmpty(options.UrlBasePath))
            {
                Console.WriteLine("\nEnter the URL base path for this docset, for example, '/dotnet' or '/windows/uwp':\n");
                options.UrlBasePath = Console.ReadLine();
            }

            docFxRepo.UrlBasePath = options.UrlBasePath;

            Console.WriteLine($"\nSearching the '{options.ArticlesDirectory}' directory for links to redirected topics...\n");

            // Gather all the redirects.
            List<Redirect> redirects = docFxRepo.GetAllRedirects();

            // Get all the markdown and YAML files.
            List<FileInfo> linkingFiles = GetMarkdownFiles(options.ArticlesDirectory);
            linkingFiles.AddRange(GetYAMLFiles(options.ArticlesDirectory));

            // Check all links, including in toc.yml, to files in the redirects list.
            // Replace links to redirected files.
            docFxRepo.ReplaceRedirectedLinks(redirects, linkingFiles);

            Console.WriteLine("\nFinished replacing redirected links.");
        }

        // Replace site-relative links to *this* repo with file-relative links.
        if (options.ReplaceWithRelativeLinks)
        {
            // Get the directory that represents the docset.
            if (String.IsNullOrEmpty(options.ArticlesDirectory))
            {
                Console.WriteLine("\nEnter the path to the directory that contains the articles with links to fix up:\n");
                options.ArticlesDirectory = Console.ReadLine();
            }
            if (String.IsNullOrEmpty(options.ArticlesDirectory) || !Directory.Exists(options.ArticlesDirectory))
            {
                Console.WriteLine($"\nThe {options.ArticlesDirectory} directory doesn't exist.");
                return;
            }

            // Make sure the searchable directory is part of the same DocFx docset.
            if (!options.ArticlesDirectory.IsSubdirectoryOf(docFxRepo.DocFxDirectory.FullName))
            {
                Console.WriteLine($"'{options.ArticlesDirectory}' is not a child of the docfx.json file's directory '{docFxRepo.DocFxDirectory}'.");
                return;
            }

            // Check that this isn't the root directory of the repo. The code doesn't handle that case currently
            // because it can't always determine the base path of the docset (e.g. for dotnet/docs repo).
            if (String.Equals(docFxRepo.OpsConfigFile.DirectoryName, options.ArticlesDirectory, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"\nYou entered the repo root directory. Please enter a subdirectory in which to replace links.");
                return;
            }

            // Get the absolute path to the base directory for this docset.
            string rootDirectory = docFxRepo.GetDocsetAbsolutePath(options.ArticlesDirectory);

            if (String.IsNullOrEmpty(options.UrlBasePath))
            {
                Console.WriteLine("\nEnter the URL base path for this docset, for example, '/dotnet' or '/windows/uwp':\n");
                options.UrlBasePath = Console.ReadLine();
            }

            docFxRepo.UrlBasePath = options.UrlBasePath;

            Console.WriteLine($"\nReplacing site-relative links to '{docFxRepo.UrlBasePath}/' in " +
                $"the '{options.ArticlesDirectory}' directory with file-relative links.\n");

            // Get all the markdown and YAML files in the search directory.
            List<FileInfo> linkingFiles = GetMarkdownFiles(options.ArticlesDirectory);
            linkingFiles.AddRange(GetYAMLFiles(options.ArticlesDirectory));

            // Check all links in these files.
            ReplaceLinks(linkingFiles, docFxRepo.UrlBasePath, rootDirectory);

            Console.WriteLine("\nFinished fixing relative links.");
        }

        // Remove hops/daisy chains in a redirection file.        
        if (options.RemoveRedirectHops)
        {
            // Get the directory that represents the docset.
            if (String.IsNullOrEmpty(options.DocFxDirectory))
            {
                Console.WriteLine("\nEnter the path to the directory that contains the docfx.json file:\n");
                options.DocFxDirectory = Console.ReadLine();
            }
            if (String.IsNullOrEmpty(options.DocFxDirectory) || !Directory.Exists(options.DocFxDirectory))
            {
                Console.WriteLine($"\nThe {options.DocFxDirectory} directory doesn't exist.");
                return;
            }

            // Make sure the searchable directory is part of the same DocFx docset.
            // These can be different if docFxRepo was constructed using a different directory (e.g. articles/media/snippets/include).
            if (!options.DocFxDirectory.IsSubdirectoryOf(docFxRepo.DocFxDirectory.FullName))
            {
                Console.WriteLine($"'{options.DocFxDirectory}' is not a child of the docfx.json file's directory '{docFxRepo.DocFxDirectory}'.");
                return;
            }

            docFxRepo.RemoveAllRedirectHops();

            Console.WriteLine("\nFinished removing redirect hops.");
        }

        // Nothing to do.
        if (options.FindOrphanedArticles is false &&
            options.FindOrphanedImages is false &&
            options.CatalogImages is false &&
            options.FindOrphanedIncludes is false &&
            options.FindOrphanedSnippets is false &&
            options.ReplaceRedirectTargets is false &&
            options.ReplaceWithRelativeLinks is false &&
            options.RemoveRedirectHops is false)
        {
            Console.WriteLine("\nYou didn't specify which function to perform. To see options, use 'CleanRepo.exe -?'.");
            return;
        }

        stopwatch.Stop();
        Console.WriteLine($"\nElapsed time: {stopwatch.Elapsed.ToHumanReadableString()}");
    }

    #region Replace site-relative links
    private static void ReplaceLinks(List<FileInfo> linkingFiles, string urlBasePath, string rootDirectory)
    {
        // Strip preceding / off urlBasePath, if it exists.
        urlBasePath = urlBasePath.TrimStart('/');

        foreach (var linkingFile in linkingFiles)
        {
            // Read the whole file up front because we might change the file mid-flight.
            string originalFileText = File.ReadAllText(linkingFile.FullName);

            // Test strings:
            // ![VisualizerIcon](/test-repo/debugger/dbg-tips.png "Visualizer icon")
            // ![VisualizerIcon](/test-repo/debugger/dbg-tips.png)
            // For more information, see [this page](/test-repo/debugger/dbg-tips).

            // Find links that look like [link text](/docsetName/some other text)
            string pattern1 = @"\]\(<?(/" + urlBasePath + @"/([^\)\s]*)>?)\)";

            foreach (Match match in Regex.Matches(originalFileText, pattern1, RegexOptions.IgnoreCase))
            {
                // Get the first capture group, which is the part of the path after the docset name.
                string siteRelativePath = match.Groups[2].Value;

                // If the path contains a ?, ignore this link as replacing it might not be ideal.
                // For example, if the link is to a specific version like "?view=vs-2015".
                if (siteRelativePath.IndexOf('?') >= 0)
                    continue;

                ReplaceLinkText(siteRelativePath, rootDirectory, linkingFile, match.Groups[0].Value, match.Groups[1].Value);
            }

            // Find links that look like <img src="/azure/mydocs/media/pic3.png">
            string pattern2 = "<img[^>]*?src[ ]*=[ ]*\"(/" + urlBasePath + "/([^>]*?.(png|gif|jpg|svg)))[ ]*\"";

            foreach (Match match in Regex.Matches(originalFileText, pattern2, RegexOptions.IgnoreCase))
            {
                // Get the first capture group, which is the part of the path after the docset name.
                string siteRelativePath = match.Groups[2].Value;

                ReplaceLinkText(siteRelativePath, rootDirectory, linkingFile, match.Groups[0].Value, match.Groups[1].Value);
            }

            // Find links that look like [0]: /azure/mydocs/media/pic1.png
            string pattern3 = @"\[.*\]:[ ]*(/" + urlBasePath + @"/(.*\.(png|gif|jpg|svg)))";

            foreach (Match match in Regex.Matches(originalFileText, pattern3, RegexOptions.IgnoreCase))
            {
                // Get the first capture group, which is the part of the path after the docset name.
                string siteRelativePath = match.Groups[2].Value;

                ReplaceLinkText(siteRelativePath, rootDirectory, linkingFile, match.Groups[0].Value, match.Groups[1].Value);
            }

            // Find links that look like imageSrc: /azure/mydocs/media/pic1.png
            string pattern4 = @"imageSrc:[ ]*(/" + urlBasePath + @"/([^:]*\.(png|gif|jpg|svg)))";

            foreach (Match match in Regex.Matches(originalFileText, pattern4, RegexOptions.IgnoreCase))
            {
                // Get the first capture group, which is the part of the path after the docset name.
                string siteRelativePath = match.Groups[2].Value;

                ReplaceLinkText(siteRelativePath, rootDirectory, linkingFile, match.Groups[0].Value, match.Groups[1].Value);
            }

            // Find links that look like :::image type="complex" source="/azure/mydocs/media/pic1.png" alt-text="Screenshot.":::
            string pattern5 = @":::image[^:]*source=""(/" + urlBasePath + @"/([^:]*\.(png|gif|jpg|svg)))""[^:]*:::";

            foreach (Match match in Regex.Matches(originalFileText, pattern5, RegexOptions.IgnoreCase))
            {
                // Get the first capture group, which is the part of the path after the docset name.
                string siteRelativePath = match.Groups[2].Value;

                ReplaceLinkText(siteRelativePath, rootDirectory, linkingFile, match.Groups[0].Value, match.Groups[1].Value);
            }
        }
    }

    private static void ReplaceLinkText(string siteRelativePath, string rootDirectory, FileInfo linkingFile, string originalMatch, string originalLink)
    {
        // If the link contains a bookmark, trim it off and add it back later.
        // If there are two hash characters, this pattern is greedy and finds the last one.
        string bookmarkPattern = @"(.*)(#.*)";
        string bookmark = null;
        if (Regex.IsMatch(siteRelativePath, bookmarkPattern))
        {
            Match bookmarkMatch = Regex.Match(siteRelativePath, bookmarkPattern);
            siteRelativePath = bookmarkMatch.Groups[1].Value;
            bookmark = bookmarkMatch.Groups[2].Value;
        }

        // Build an absolute path to this file.
        string absolutePath = Path.Combine(rootDirectory, siteRelativePath.Trim());

        // Clean up the path by replacing forward slashes with back slashes, removing extra dots, etc.
        absolutePath = Path.GetFullPath(absolutePath);

        FileInfo file = new FileInfo(absolutePath);
        if (String.IsNullOrEmpty(file.Extension) || !string.Equals(file.Extension, ".md") || !string.Equals(file.Extension, ".yml"))
        {
            // Look for a file of this name in the same directory to obtain its extension.
            try
            {
                FileInfo[] files = file.Directory.GetFiles(file.Name + ".*");
                if (files.Length > 0)
                {
                    // Since site-relative image links still require a file extension,
                    // and this link didn't include an extension, favor a non-image extension first.
                    if (files.Any(f => f.Extension == ".md"))
                        absolutePath = files.First(f => f.Extension == ".md").FullName;
                    else if (files.Any(f => f.Extension == ".yml"))
                        absolutePath = files.First(f => f.Extension == ".yml").FullName;
                    else
                        absolutePath = files[0].FullName;
                }
            }
            catch (DirectoryNotFoundException)
            {
                // This can happen if files from a different repo map to the same docset.
                // For example, the C# language specification: [C# Language Specification](/dotnet/csharp/language-reference/language-specification/introduction)
                return;
            }
        }

        // Check that the link is valid in the local repo.
        if (!File.Exists(absolutePath))
        {
            return;
        }

        if (absolutePath != null)
        {
            // Determine the file-relative path to absolutePath.
            string fileRelativePath = Path.GetRelativePath(linkingFile.DirectoryName, absolutePath);

            // Replace backslashes with forward slashes.
            fileRelativePath = fileRelativePath.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (fileRelativePath != null)
            {
                // Add the bookmark back onto the end, if there is one.
                if (!String.IsNullOrEmpty(bookmark))
                {
                    fileRelativePath = fileRelativePath + bookmark;
                }

                string newText = originalMatch.Replace(originalLink, fileRelativePath);

                // Replace the link.
                Console.WriteLine($"Replacing '{originalMatch}' with '{newText}' in file '{linkingFile.FullName}'.");

                // If a previous link was found and replaced, the text may have changed, so reread the file.
                string currentFileText = File.ReadAllText(linkingFile.FullName);

                File.WriteAllText(linkingFile.FullName, currentFileText.Replace(originalMatch, newText));
            }
        }
    }
    #endregion

    #region Orphaned includes
    /// TODO: Improve the perf of this method using the following pseudo code:
    /// For each include file
    ///    For each markdown file
    ///       Do a RegEx search for the include file
    ///          If found, BREAK to the next include file
    private static void ListOrphanedIncludes(string inputDirectory, Dictionary<string, int> includeFiles, bool deleteOrphanedIncludes)
    {
        // Get all files that could possibly link to the include files
        var files = GetAllMarkdownFiles(inputDirectory, out DirectoryInfo rootDirectory);

        if (files is null)
        {
            return;
        }

        // Gather up all the include references and increment the count for that include file in the Dictionary.
        //foreach (var markdownFile in files)
        Parallel.ForEach(files, markdownFile =>
        {
            foreach (string line in File.ReadAllLines(markdownFile.FullName))
            {
                // Example include references:
                // [!INCLUDE [DotNet Restore Note](../includes/dotnet-restore-note.md)]
                // [!INCLUDE[DotNet Restore Note](~/includes/dotnet-restore-note.md)]
                // [!INCLUDE [temp](../_shared/assign-to-sprint.md)]

                // An include file referenced from another include file won't have "includes" or "_shared" in the path.
                // E.g. [!INCLUDE [P2S FAQ All](vpn-gateway-faq-p2s-all-include.md)]

                // RegEx pattern to match
                string includeLinkPattern = @"\[!INCLUDE[ ]?\[[^\]]*?\]\(<?(.*?\.md)";

                // There could be more than one INCLUDE reference on the line, hence the foreach loop.
                foreach (Match match in Regex.Matches(line, includeLinkPattern, RegexOptions.IgnoreCase))
                {
                    // Get the first capture group, which is the relative path ending in '.md'.
                    string relativePath = match.Groups[1].Value.Trim();

                    if (relativePath != null)
                    {
                        string fullPath;

                        // Path could start with a tilde e.g. ~/includes/dotnet-restore-note.md
                        if (relativePath.StartsWith("~/"))
                        {
                            fullPath = Path.Combine(rootDirectory.FullName, relativePath.TrimStart('~', '/'));
                        }
                        else
                        {
                            // Construct the full path to the referenced INCLUDE file
                            fullPath = Path.Combine(markdownFile.DirectoryName, relativePath);
                        }

                        // Clean up the path by replacing forward slashes with back slashes, removing extra dots, etc.
                        fullPath = Path.GetFullPath(fullPath);

                        if (fullPath != null)
                        {
                            // Increment the count for this INCLUDE file in our dictionary
                            try
                            {
                                // TODO - is this okay to have in a Parallel.ForEach loop?
                                includeFiles[fullPath.ToLower()]++;
                            }
                            catch (KeyNotFoundException)
                            {
                                // No need to do anything.
                            }
                        }
                    }
                }
            }
        });

        int count = 0;

        // Print out the INCLUDE files that have zero references.
        StringBuilder output = new StringBuilder();
        foreach (var includeFile in includeFiles)
        {
            if (includeFile.Value == 0)
            {
                count++;
                output.AppendLine(Path.GetFullPath(includeFile.Key));
            }
        }

        if (deleteOrphanedIncludes)
        {
            // Delete orphaned image files
            foreach (var includeFile in includeFiles)
            {
                if (includeFile.Value == 0)
                {
                    File.Delete(includeFile.Key);
                }
            }
        }

        string deleted = deleteOrphanedIncludes ? "and deleted " : "";

        Console.WriteLine($"\nFound {deleted}{count} orphaned INCLUDE files:\n");
        Console.WriteLine(output.ToString());
        Console.WriteLine("DONE");
    }

    /// <summary>
    /// Returns a collection of *.md files in the current directory, and optionally subdirectories,
    /// if the directory name is 'includes' or '_shared'.
    /// </summary>
    /// <returns></returns>
    private static Dictionary<string, int> GetIncludeFiles(string inputDirectory)
    {
        DirectoryInfo dir = new DirectoryInfo(inputDirectory);

        Dictionary<string, int> includeFiles = new Dictionary<string, int>();

        if (String.Compare(dir.Name, "includes", true) == 0 || String.Compare(dir.Name, "_shared", true) == 0)
        {
            // This is a folder that is likely to contain "include"-type files, i.e. files that aren't in the TOC.

            foreach (var file in dir.EnumerateFiles("*.md"))
            {
                includeFiles.Add(file.FullName.ToLower(), 0);
            }
        }

        // Search in subdirectories.
        foreach (var subDirectory in dir.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            if (String.Compare(subDirectory.Name, "includes", true) == 0 || String.Compare(subDirectory.Name, "_shared", true) == 0)
            {
                // This is a folder that is likely to contain "include"-type files, i.e. files that aren't in the TOC.

                foreach (var file in subDirectory.EnumerateFiles("*.md"))
                {
                    includeFiles.Add(file.FullName.ToLower(), 0);
                }
            }
        }

        return includeFiles;
    }
    #endregion

    #region Orphaned snippets
    /// <summary>
    /// Returns a list of *.cs and *.vb files in the current directory, and optionally subdirectories.
    /// </summary>
    private static List<string> GetSnippetFiles(string inputDirectory)
    {
        DirectoryInfo dir = new DirectoryInfo(inputDirectory);

        List<string> snippetFiles = new List<string>();

        foreach (var file in dir.EnumerateFiles("*.cs"))
        {
            snippetFiles.Add(file.FullName.ToLower());
        }
        foreach (var file in dir.EnumerateFiles("*.vb"))
        {
            snippetFiles.Add(file.FullName.ToLower());
        }

        foreach (var subDirectory in dir.EnumerateDirectories("*", SearchOption.AllDirectories))
        {
            foreach (var file in subDirectory.EnumerateFiles("*.cs"))
            {
                snippetFiles.Add(file.FullName.ToLower());
            }
            foreach (var file in subDirectory.EnumerateFiles("*.vb"))
            {
                snippetFiles.Add(file.FullName.ToLower());
            }
        }

        return snippetFiles;
    }

    /// <summary>
    /// Builds a list of solution files and all the (unique) project directories they reference (using full paths).
    /// </summary>
    private static List<(string, List<string>)> GetSolutionFiles(string startDirectory)
    {
        List<(string, List<string>)> solutionFiles = new List<(string, List<string>)>();

        DirectoryInfo dir = new DirectoryInfo(startDirectory);
        foreach (var slnFile in dir.EnumerateFiles("*.sln", SearchOption.AllDirectories))
        {
            SolutionFile solutionFile = SolutionFile.Parse(slnFile.FullName);
            List<string> projectFiles = solutionFile.ProjectsInOrder.Select(p => Path.GetDirectoryName(p.AbsolutePath).ToLowerInvariant()).Distinct().ToList();

            solutionFiles.Add((slnFile.FullName, projectFiles));
        }

        return solutionFiles;
    }

    private static void ListOrphanedSnippets(string inputDirectory, List<string> snippetFiles,
        List<(string, List<string>)> solutionFiles, bool deleteOrphanedSnippets)
    {
        // Get all files that could possibly link to the snippet files
        var files = GetAllMarkdownFiles(inputDirectory, out DirectoryInfo rootDirectory);

        if (files is null)
        {
            return;
        }

        Console.WriteLine($"Checking {snippetFiles.Count} snippet files.");

        int countOfOrphans = 0;
        // Prints out the snippet files that have zero references.
        StringBuilder output = new StringBuilder();

        // Keep track of which directories are referenced/unreferenced.
        Dictionary<string, int> snippetDirectories = new Dictionary<string, int>();

        foreach (var snippetFile in snippetFiles)
        {
            FileInfo fi = new FileInfo(snippetFile);
            string snippetFileName = fi.Name;

            bool foundSnippetReference = false;

            // Check if there's a .csproj or .vbproj file in its ancestry.
            bool partOfProject = false;
            DirectoryInfo projectDir = GetDirectory(new DirectoryInfo(fi.DirectoryName), "*.??proj");
            if (projectDir != null)
                partOfProject = true;

            if (!partOfProject)
            {
                foreach (FileInfo markdownFile in files)
                {
                    // Matches the following types of snippet syntax:
                    // :::code language="csharp" source="snippets/EventCounters/MinimalEventCounterSource.cs":::
                    // [!code-csharp[Violation#1](../code-quality/codesnippet/ca1010.cs)]
                    // [!code-csharp[Violation#1](../code-quality/codesnippet/ca1010.cs#snippet1)]
                    // [!code-csharp[Hi](./code/code.cs?highlight=1,6)]
                    // [!code-csharp[FxCop.Usage#1](./code/code.cs?range=3-6)]

                    string regex = @"(\(|"")([^\)""\n]*\/" + snippetFileName + @")(#\w*)?(\?\w*=(\d|,|-)*)?(\)|"")";

                    foreach (Match match in Regex.Matches(File.ReadAllText(markdownFile.FullName), regex, RegexOptions.IgnoreCase))
                    {
                        if (!(match is null) && match.Length > 0)
                        {
                            string relativePath = match.Groups[2].Value.Trim();

                            if (relativePath != null)
                            {
                                string fullPath;

                                // Path could start with a tilde e.g. ~/snippets/ca1010.cs
                                if (relativePath.StartsWith("~/"))
                                {
                                    fullPath = Path.Combine(rootDirectory.FullName, relativePath.TrimStart('~', '/'));
                                }
                                else
                                {
                                    // Construct the full path to the referenced snippet file
                                    fullPath = Path.Combine(markdownFile.DirectoryName, relativePath);
                                }

                                // Clean up the path by replacing forward slashes with back slashes, removing extra dots, etc.
                                fullPath = Path.GetFullPath(fullPath);

                                if (String.Equals(snippetFile, fullPath, StringComparison.InvariantCultureIgnoreCase))
                                {
                                    // This snippet file is not orphaned.
                                    foundSnippetReference = true;
                                    break;
                                }
                            }
                        }
                    }

                    if (foundSnippetReference)
                        break;
                    // else check the next Markdown file.
                }

                if (!foundSnippetReference)
                {
                    // The snippet file is orphaned (not used anywhere).
                    countOfOrphans++;
                    output.AppendLine(Path.GetFullPath(snippetFile));

                    if (deleteOrphanedSnippets)
                    {
                        File.Delete(snippetFile);
                    }
                }
            }
            else
            {
                // The code file is part of a project.
                // If any descendants of the project file directory
                // are referenced, then don't delete anything in the project file directory.

                // If we've already determined this project directory is orphaned or unorphaned, move on.
                if (snippetDirectories.ContainsKey(projectDir.FullName))
                    continue;

                foreach (FileInfo markdownFile in files)
                {
                    // Matches the following types of snippet syntax:
                    // :::code language="csharp" source="snippets/EventCounters/MinimalEventCounterSource.cs":::
                    // [!code-csharp[Violation#1](../code-quality/codesnippet/CSharp/ca1010.cs)]

                    // Search for a reference that includes the project directory name.
                    string regex = @"(\(|"")([^\)""\n]*" + projectDir.Name + @")\/[^\)""\n]*(\)|"")";

                    // Loop through all the matches in the file.
                    MatchCollection matches = Regex.Matches(File.ReadAllText(markdownFile.FullName), regex, RegexOptions.IgnoreCase);
                    foreach (Match match in matches)
                    {
                        if (!(match is null) && match.Length > 0)
                        {
                            string relativePath = match.Groups[2].Value.Trim();

                            if (relativePath != null)
                            {
                                string fullPath;

                                // Path could start with a tilde e.g. ~/snippets/stuff
                                if (relativePath.StartsWith("~/"))
                                {
                                    fullPath = Path.Combine(rootDirectory.FullName, relativePath.TrimStart('~', '/'));
                                }
                                else
                                {
                                    // Construct the full path to the referenced directory.
                                    fullPath = Path.Combine(markdownFile.DirectoryName, relativePath);
                                }

                                // Clean up the path by replacing forward slashes with back slashes, removing extra dots, etc.
                                fullPath = Path.GetFullPath(fullPath);

                                if (String.Equals(projectDir.FullName, fullPath, StringComparison.InvariantCultureIgnoreCase))
                                {
                                    // This snippet file is not orphaned.
                                    foundSnippetReference = true;

                                    // Add the project directory to the known list of directories to keep (saves searching again).
                                    if (!snippetDirectories.ContainsKey(projectDir.FullName))
                                        snippetDirectories.Add(projectDir.FullName, 1);
                                    else
                                        snippetDirectories[projectDir.FullName]++;

                                    break;
                                }
                            }
                        }
                    }

                    if (foundSnippetReference)
                        break;
                    // else check the next Markdown file.
                }

                if (!foundSnippetReference)
                {
                    // The snippet file and its project directory is orphaned (not used anywhere).
                    // Set reference count to 0;
                    if (!snippetDirectories.ContainsKey(projectDir.FullName))
                    {
                        snippetDirectories.Add(projectDir.FullName, 0);
                    }
                }
            }
        }

        // Output info for non-project snippets.
        Console.WriteLine($"\nFound {countOfOrphans} orphaned snippet files:\n");
        Console.WriteLine(output.ToString());

        StringBuilder dirSlnOutput = new StringBuilder("The following project directories are orphaned:\n\n");

        // Delete orphaned directories.
        IEnumerable<string> directoriesToDelete = snippetDirectories.Where(kvp => kvp.Value == 0).Select(kvp => kvp.Key);

        foreach (var directory in directoriesToDelete)
        {
            bool isPartOfSolution = false;

            // Check if the directory is referenced in a solution file.
            // If so, we'll (possibly) delete it in the next step.
            foreach (var slnFile in solutionFiles)
            {
                if (slnFile.Item2.Contains(directory, StringComparer.InvariantCultureIgnoreCase))
                {
                    isPartOfSolution = true;
                    break;
                }
            }

            if (!isPartOfSolution)
            {
                dirSlnOutput.AppendLine(directory);
                if (deleteOrphanedSnippets)
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        dirSlnOutput.AppendLine("\nThe following solution directories are orphaned:\n");

        // Delete orphaned solutions.
        foreach (var solutionFile in solutionFiles)
        {
            // Check if any of its projects (snippets) are referenced anywhere.
            bool isReferenced = false;

            foreach (var projectDir in solutionFile.Item2)
            {
                if (snippetDirectories.TryGetValue(projectDir, out int refCount))
                {
                    if (refCount > 0)
                    {
                        isReferenced = true;
                        break;
                    }
                }
            }

            if (!isReferenced)
            {
                string path = Path.GetDirectoryName(solutionFile.Item1);
                dirSlnOutput.AppendLine(path);
                if (deleteOrphanedSnippets)
                {
                    // Delete the solution and its directory.
                    Directory.Delete(path, true);
                }
            }
        }

        // Output info for unreferenced projects and solutions.
        Console.WriteLine(dirSlnOutput.ToString());
    }
    #endregion

    private static string TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (PathTooLongException)
        {
            Console.WriteLine($"Unable to get path because it's too long: {path}\n");
            return null;
        }
    }

    #region Orphaned articles
    /// <summary>
    /// Lists the markdown files that aren't referenced from a TOC file.
    /// </summary>
    private static void ListOrphanedTopics(List<FileInfo> tocFiles, List<FileInfo> markdownFiles, bool deleteOrphanedTopics)
    {
        Dictionary<string, int> filesToKeep = new Dictionary<string, int>();

        Console.WriteLine("\nTopics not in any TOC file (that are also not includes or shared or misc):\n");

        bool IsArticleFile(FileInfo file) =>
            !file.FullName.Contains("\\includes\\") &&
            !file.FullName.Contains("\\_shared\\") &&
            !file.FullName.Contains("\\misc\\") &&
            String.Compare(file.Name, "TOC.md", StringComparison.InvariantCultureIgnoreCase) != 0 &&
            String.Compare(file.Name, "index.md", StringComparison.InvariantCultureIgnoreCase) != 0;

        List<FileInfo> orphanedFiles = new List<FileInfo>();

        Parallel.ForEach(markdownFiles.Where(IsArticleFile), markdownFile =>
        {
            var found = tocFiles.Any(tocFile => IsFileLinkedFromTocFile(markdownFile, tocFile));
            if (!found)
            {
                orphanedFiles.Add(markdownFile);
                Console.WriteLine(markdownFile.FullName);
            }
        });

        Console.WriteLine($"\nFound {orphanedFiles.Count} .md files that aren't referenced in a TOC.");

        // Delete files if the option is set.
        if (deleteOrphanedTopics)
        {
            Parallel.ForEach(markdownFiles, linkingFile =>
            {
                CheckFileLinks(orphanedFiles, linkingFile, ref filesToKeep);
            });

            // Delete files that aren't linked to.
            foreach (var orphanedFile in orphanedFiles)
            {
                if (!filesToKeep.ContainsKey(orphanedFile.FullName))
                    File.Delete(orphanedFile.FullName);
            }

            if (filesToKeep.Count > 0)
            {
                Console.Write($"\nThe following {filesToKeep.Count} files *were not deleted* " +
                    $"because they're referenced in one or more files:\n\n");
                foreach (var fileName in filesToKeep)
                {
                    Console.WriteLine(fileName);
                }
            }
            else
            {
                Console.WriteLine($"\nDeleted {orphanedFiles.Count} files.");
            }
        }
    }

    private static bool IsFileLinkedFromTocFile(FileInfo linkedFile, FileInfo tocFile)
    {
        string text = File.ReadAllText(tocFile.FullName);

        // Example links .yml/.md:
        // href: ide/managing-external-tools.md
        // # [Managing External Tools](ide/managing-external-tools.md)

        string linkRegEx = tocFile.Extension.ToLower() == ".yml" ?
            @"href:(.*?" + linkedFile.Name + ")" :
            @"\]\(<?(([^\)])*?" + linkedFile.Name + @")";

        // For each link that contains the file name...
        foreach (Match match in Regex.Matches(text, linkRegEx, RegexOptions.IgnoreCase))
        {
            // Get the file-relative path to the linked file.
            string relativePath = match.Groups[1].Value.Trim();

            // Remove any quotation marks
            relativePath = relativePath.Replace("\"", "");

            if (relativePath.StartsWith("/") || relativePath.StartsWith("http:") || relativePath.StartsWith("https:"))
            {
                // The file is in a different repo, so ignore it.
                continue;

                // TODO - For links that start with "/", check if they are in the same repo.
            }

            if (relativePath != null)
            {
                // Construct the full path to the referenced file
                string fullPath = Path.Combine(tocFile.DirectoryName, relativePath);

                // This cleans up the path by replacing forward slashes with back slashes, removing extra dots, etc.
                fullPath = Path.GetFullPath(fullPath);

                if (fullPath != null)
                {
                    // See if our constructed path matches the actual file we think it is
                    if (String.Compare(fullPath, linkedFile.FullName, true) == 0)
                    {
                        return true;
                    }
                    else
                    {
                        // If we get here, the file name matched but the full path did not.
                    }
                }
            }
        }

        // We did not find this file linked in the specified file.
        return false;
    }

    /// <summary>
    /// If linkingFile contains a link to any file in linkedFiles, add the file to filesToKeep.
    /// </summary>
    private static void CheckFileLinks(List<FileInfo> linkedFiles, FileInfo linkingFile, ref Dictionary<string, int> filesToKeep)
    {
        if (!File.Exists(linkingFile.FullName))
        {
            return;
        }

        string fileContents = File.ReadAllText(linkingFile.FullName);

        // Example links .yml/.md:
        // href: ide/managing-external-tools.md
        // [Managing External Tools](ide/managing-external-tools.md)

        foreach (var linkedFile in linkedFiles)
        {
            string linkRegEx = linkingFile.Extension.ToLower() == ".yml" ?
                    @"href:(.*?" + linkedFile.Name + ")" :
                    @"\]\(<?(([^\)])*?" + linkedFile.Name + @")";

            // For each link that contains the file name...
            foreach (Match match in Regex.Matches(fileContents, linkRegEx, RegexOptions.IgnoreCase))
            {
                // Get the file-relative path to the linked file.
                string relativePath = match.Groups[1].Value.Trim();

                // Remove any quotation marks
                relativePath = relativePath.Replace("\"", "");

                if (relativePath != null)
                {
                    string fullPath;
                    try
                    {
                        // Construct the full path to the referenced file
                        fullPath = Path.Combine(linkingFile.DirectoryName, relativePath);
                    }
                    catch (ArgumentException e)
                    {
                        Console.WriteLine($"\nCaught exception while constructing full path " +
                            $"for '{relativePath}' in '{linkingFile.FullName}': {e.Message}");
                        throw;
                    }

                    // This cleans up the path by replacing forward slashes with back slashes, removing extra dots, etc.
                    fullPath = Path.GetFullPath(fullPath);
                    if (fullPath != null)
                    {
                        // See if our constructed path matches the actual file we think it is
                        if (String.Compare(fullPath, linkedFile.FullName, true) == 0)
                        {
                            // File is linked from another file.
                            if (filesToKeep.ContainsKey(linkedFile.FullName))
                            {
                                // Increment the count of links to this file.
                                filesToKeep[linkedFile.FullName]++;
                            }
                            else
                            {
                                filesToKeep.Add(linkedFile.FullName, 1);
                            }
                        }
                        else
                        {
                            // This link did not match the full file name.
                        }
                    }
                }
            }
        }
    }
    #endregion

    #region Popular files
    /// <summary>
    /// Finds topics that appear more than once, either in one TOC.md file, or multiple TOC.md files.
    /// </summary>
    private static void ListPopularFiles(List<FileInfo> tocFiles, List<FileInfo> markdownFiles)
    {
        bool found = false;
        StringBuilder output = new StringBuilder("The following files appear in more than one TOC file:\n\n");

        // Keep a hash table of each topic path with the number of times it's referenced
        Dictionary<string, int> topics = markdownFiles.ToDictionary<FileInfo, string, int>(mf => mf.FullName, mf => 0);

        foreach (var markdownFile in markdownFiles)
        {
            // If the file is in the Includes directory, ignore it
            if (markdownFile.FullName.Contains("\\includes\\"))
                continue;

            foreach (var tocFile in tocFiles)
            {
                if (IsFileLinkedFromFile(markdownFile, tocFile))
                {
                    topics[markdownFile.FullName]++;
                }
            }
        }

        // Now spit out the topics that appear more than once.
        foreach (var topic in topics)
        {
            if (topic.Value > 1)
            {
                found = true;
                output.AppendLine(topic.Key);
            }
        }

        // Only write the StringBuilder to the console if we found a topic referenced from more than one TOC file.
        if (found)
        {
            Console.Write(output.ToString());
        }
    }
    #endregion

    #region Generic helper methods
    /// <summary>
    /// Looks for the specified file in the specified directory, and if not found,
    /// in all parent directories up to the disk root directory.
    /// </summary>
    public static FileInfo GetFileHereOrInParent(string inputDirectory, string fileName)
    {
        DirectoryInfo dir = new DirectoryInfo(inputDirectory);

        try
        {
            while (dir.GetFiles(fileName, SearchOption.TopDirectoryOnly).Length == 0)
            {
                dir = dir.Parent;

                // Loop exit condition.
                if (dir == dir.Root)
                    return null;
            }

            return dir.GetFiles(fileName, SearchOption.TopDirectoryOnly)[0];
        }
        catch (DirectoryNotFoundException)
        {
            Console.WriteLine($"Could not find directory {dir.FullName}");
            throw;
        }
    }

    /// <summary>
    /// Returns true if 'child' is a subdirectory (or the same directory) as 'other'.
    /// </summary>
    public static bool IsSubdirectoryOf(this string child, string other)
    {
        bool isChild = false;

        var childInfo = new DirectoryInfo(child);
        var otherInfo = new DirectoryInfo(other);

        if (childInfo.FullName == otherInfo.FullName) { return true; }

        while (childInfo.Parent != null)
        {
            if (childInfo.Parent.FullName == otherInfo.FullName)
            {
                isChild = true;
                break;
            }
            else childInfo = childInfo.Parent;
        }

        return isChild;
    }

    // The base path should be obtained through a user-provided option instead.
    //internal static string GetUrlBasePath(DirectoryInfo docFxDirectory)
    //{
    //    string docfxFilePath = Path.Combine(docFxDirectory.FullName, "docfx.json");
    //    string urlBasePath = null;

    //    // Deserialize the docfx.json file.
    //    DocFx docfx = LoadDocfxFile(docfxFilePath);
    //    if (docfx == null)
    //    {
    //        return null;
    //    }

    //    // Hack: Parse URL base path out of breadcrumbPath. Examples:
    //    // "breadcrumb_path": "/visualstudio/_breadcrumb/toc.json"
    //    // "breadcrumb_path": "/windows/uwp/breadcrumbs/toc.json"
    //    // "breadcrumb_path": "/dotnet/breadcrumb/toc.json"
    //    // "breadcrumb_path": "breadcrumb/toc.yml"  <--Need to handle this.

    //    string? breadcrumbPath = docfx.build.globalMetadata.breadcrumb_path;

    //    if (breadcrumbPath is not null)
    //    {
    //        // Remove everything after and including the second last / character.
    //        if (breadcrumbPath.Contains('/'))
    //        {
    //            breadcrumbPath = breadcrumbPath[0..breadcrumbPath.LastIndexOf('/')];
    //            if (breadcrumbPath.Contains('/'))
    //            {
    //                urlBasePath = breadcrumbPath[0..breadcrumbPath.LastIndexOf('/')];
    //            }
    //        }
    //    }

    //    if (!String.IsNullOrEmpty(urlBasePath))
    //    {
    //        Console.WriteLine($"Is '{urlBasePath}' the correct URL base path for your docs (y or n)?");
    //        char key = Console.ReadKey().KeyChar;

    //        if (key == 'y' || key == 'Y')
    //        {
    //            Console.WriteLine("\nThanks!");
    //            return urlBasePath;
    //        }
    //    }

    //    Console.WriteLine($"\nWhat's the URL base path for articles in the `{docFxDirectory.FullName}` directory? (Example: /aspnet/core)");
    //    string basePath = Console.ReadLine();
    //    Console.WriteLine("\nThanks!");
    //    return basePath;
    //}

    /// <summary>
    /// Checks if the specified file path is referenced in the specified file.
    /// </summary>
    private static bool IsFileLinkedFromFile(FileInfo linkedFile, FileInfo linkingFile)
    {
        if (!File.Exists(linkingFile.FullName))
        {
            return false;
        }

        foreach (var line in File.ReadAllLines(linkingFile.FullName))
        {
            // Example links .yml/.md:
            // href: ide/managing-external-tools.md
            // [Managing External Tools](ide/managing-external-tools.md)

            string linkRegEx = linkingFile.Extension.ToLower() == ".yml" ?
                @"href:(.*?" + linkedFile.Name + ")" :
                @"\]\(<?(([^\)])*?" + linkedFile.Name + @")";

            // For each link that contains the file name...
            foreach (Match match in Regex.Matches(line, linkRegEx, RegexOptions.IgnoreCase))
            {
                // Get the file-relative path to the linked file.
                string relativePath = match.Groups[1].Value.Trim();

                // Remove any quotation marks
                relativePath = relativePath.Replace("\"", "");

                if (relativePath != null)
                {
                    string fullPath;
                    try
                    {
                        // Construct the full path to the referenced file
                        fullPath = Path.Combine(linkingFile.DirectoryName, relativePath);
                    }
                    catch (ArgumentException e)
                    {
                        Console.WriteLine($"\nCaught exception while constructing full path " +
                            $"for '{relativePath}' in '{linkingFile.FullName}': {e.Message}");
                        throw;
                    }

                    // This cleans up the path by replacing forward slashes with back slashes, removing extra dots, etc.
                    fullPath = Path.GetFullPath(fullPath);
                    if (fullPath != null)
                    {
                        // See if our constructed path matches the actual file we think it is
                        if (String.Compare(fullPath, linkedFile.FullName, true) == 0)
                        {
                            return true;
                        }
                        else
                        {
                            // If we get here, the file name matched but the full path did not.
                        }
                    }
                }
            }
        }

        // We did not find this file linked in the specified file.
        return false;
    }

    /// <summary>
    /// Gets all *.md files recursively, starting in the specified directory.
    /// </summary>
    private static List<FileInfo> GetMarkdownFiles(string directoryPath, params string[] dirsToIgnore)
    {
        DirectoryInfo dir = new DirectoryInfo(directoryPath);
        IEnumerable<FileInfo> files = dir.EnumerateFiles("*.md", SearchOption.AllDirectories).ToList();

        if (dirsToIgnore.Length > 0)
        {
            foreach (string ignoreDir in dirsToIgnore)
            {
                string dirWithSlashes = String.Concat("\\", ignoreDir, "\\");
                files = files.Where(f => !f.DirectoryName.Contains(dirWithSlashes, StringComparison.InvariantCultureIgnoreCase));
            }
        }

        return files.ToList();
    }

    /// <summary>
    /// Gets all *.yml files recursively, starting in the specified directory.
    /// </summary>
    private static List<FileInfo> GetYAMLFiles(string directoryPath)
    {
        DirectoryInfo dir = new DirectoryInfo(directoryPath);
        return dir.EnumerateFiles("*.yml", SearchOption.AllDirectories).ToList();
    }

    /// <summary>
    /// Returns a dictionary of all .png/.jpg/.gif/.svg files in the directory.
    /// The search includes the specified directory and (optionally) all its subdirectories.
    /// </summary>
    private static Dictionary<string, List<string>> GetMediaFiles(string mediaDirectory, bool searchRecursively = true)
    {
        DirectoryInfo dir = new DirectoryInfo(mediaDirectory);

        SearchOption searchOption = searchRecursively ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        Dictionary<string, List<string>> mediaFiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        string[] fileExtensions = new string[] { "*.png", "*.jpg", "*.gif", "*.svg" };

        foreach (var extension in fileExtensions)
        {
            foreach (var file in dir.EnumerateFiles(extension, searchOption))
            {
                mediaFiles.Add(file.FullName.ToLower(), new List<string>());
            }
        }

        if (mediaFiles.Count == 0)
        {
            Console.WriteLine("\nNo .png/.jpg/.gif/.svg files were found!");
        }

        return mediaFiles;
    }

    /// <summary>
    /// Gets all *.yml files recursively, starting in the ancestor directory that contains docfx.json.
    /// </summary>
    internal static List<FileInfo> GetAllYamlFiles(string directoryPath, out DirectoryInfo rootDirectory)
    {
        // Look further up the path until we find docfx.json
        rootDirectory = GetDirectory(new DirectoryInfo(directoryPath), "docfx.json");

        if (rootDirectory is null)
            return null;

        return rootDirectory.EnumerateFiles("*.yml", SearchOption.AllDirectories).ToList();
    }

    /// <summary>
    /// Gets all *.md files recursively, starting in the ancestor directory that contains docfx.json.
    /// </summary>
    internal static List<FileInfo> GetAllMarkdownFiles(string directoryPath, out DirectoryInfo rootDirectory)
    {
        // Look further up the path until we find docfx.json
        rootDirectory = GetDirectory(new DirectoryInfo(directoryPath), "docfx.json");

        if (rootDirectory is null)
            return null;

        return rootDirectory.EnumerateFiles("*.md", SearchOption.AllDirectories).ToList();
    }

    /// <summary>
    /// Returns the specified directory if it contains a file with the specified name.
    /// Otherwise returns the nearest parent directory that contains a file with the specified name.
    /// </summary>
    internal static DirectoryInfo GetDirectory(DirectoryInfo dir, string fileName)
    {
        try
        {
            while (dir.GetFiles(fileName, SearchOption.TopDirectoryOnly).Length == 0)
            {
                dir = dir.Parent;

                if (dir == dir?.Root)
                {
                    return null;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            Console.WriteLine($"\nCould not find directory {dir.FullName}");
            return null;
        }

        return dir;
    }
    #endregion
}
