using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Diagnostics;

public class GitSubmodulesWindow : EditorWindow
{
	private Vector2 scrollPos;
	private List<GitSubmodule> submodules;

	private string addUrl = "";
	private string addBranch = "main";
	private string addName = "";

	[MenuItem("Help/Git Submodules")]
	public static void ShowWindow()
	{
		var window = GetWindow<GitSubmodulesWindow>("Git Submodules");
		window.RefreshSubmodules();
		window.Show();
	}

	private void OnGUI()
	{
		if (GUILayout.Button("Refresh"))
		{
			RefreshSubmodules();
		}

		EditorGUILayout.Space();
		EditorGUILayout.LabelField("➕ Add Submodule", EditorStyles.boldLabel);

		addUrl = EditorGUILayout.TextField("URL", addUrl);
		addBranch = EditorGUILayout.TextField("Branch", addBranch);
		addName = EditorGUILayout.TextField("Name", addName);

		if (GUILayout.Button("Add Submodule"))
		{
			AddSubmodule(addUrl, addBranch, addName);
			RefreshSubmodules();
		}

		EditorGUILayout.Space();
		EditorGUILayout.LabelField("📦 Existing Submodules", EditorStyles.boldLabel);

		if (submodules == null || submodules.Count == 0)
		{
			EditorGUILayout.HelpBox("No git submodules found.", MessageType.Info);
			return;
		}

		scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

		foreach (var sub in submodules)
		{
			EditorGUILayout.BeginVertical("box");
			EditorGUILayout.LabelField("Name:", sub.Name);
			EditorGUILayout.LabelField("Path:", sub.Path);
			EditorGUILayout.LabelField("URL:", sub.Url);
			EditorGUILayout.LabelField("Commit:", sub.CommitHash);

			EditorGUILayout.Space();
			if (GUILayout.Button("Remove This Submodule"))
			{
				if (EditorUtility.DisplayDialog("Remove Submodule",
					$"Are you sure you want to remove:\n{sub.Path} ?", "Yes", "Cancel"))
				{
					RemoveSubmodule(sub.Path);
					RefreshSubmodules();
					break;
				}
			}
			EditorGUILayout.EndVertical();
		}

		EditorGUILayout.EndScrollView();
	}

	// ---------- Core ----------
	private void RefreshSubmodules()
	{
		submodules = new List<GitSubmodule>();

		string projectRoot = Directory.GetCurrentDirectory();
		string gitModulesPath = Path.Combine(projectRoot, ".gitmodules");
		if (!File.Exists(gitModulesPath))
			return;

		string content = File.ReadAllText(gitModulesPath);
		ParseSubmodules(content, submodules);

		Dictionary<string, string> commitMap = GetSubmoduleStatuses(projectRoot);
		foreach (var sub in submodules)
		{
			if (commitMap.TryGetValue(sub.Path, out string commit))
			{
				sub.CommitHash = commit;
			}
		}
	}

	private void ParseSubmodules(string content, List<GitSubmodule> list)
	{
		var moduleRegex = new Regex(@"\[submodule ""(.+?)""\][\s\S]*?(?=\[|$)", RegexOptions.Multiline);
		var pathRegex = new Regex(@"path\s*=\s*(.+)");
		var urlRegex = new Regex(@"url\s*=\s*(.+)");

		foreach (Match match in moduleRegex.Matches(content))
		{
			string block = match.Value;
			string name = match.Groups[1].Value;

			string path = pathRegex.Match(block).Groups.Count > 1 ? pathRegex.Match(block).Groups[1].Value : "";
			string url = urlRegex.Match(block).Groups.Count > 1 ? urlRegex.Match(block).Groups[1].Value : "";

			list.Add(new GitSubmodule
			{
				Name = name,
				Path = path,
				Url = url,
				CommitHash = "(unknown)"
			});
		}
	}

	private Dictionary<string, string> GetSubmoduleStatuses(string workingDir)
	{
		var result = new Dictionary<string, string>();
		try
		{
			string output = RunGitCommand("submodule status", workingDir);

			var lineRegex = new Regex(@"^[ +-]?([0-9a-f]{7,40})\s+([^\s]+)", RegexOptions.Multiline);
			foreach (Match match in lineRegex.Matches(output))
			{
				string commit = match.Groups[1].Value;
				string path = match.Groups[2].Value;
				result[path] = commit;
			}
		}
		catch (System.Exception ex)
		{
			UnityEngine.Debug.LogWarning("Failed to run 'git submodule status': " + ex.Message);
		}
		return result;
	}

	// ---------- Add ----------
	private void AddSubmodule(string url, string branch, string name)
	{
		if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(name))
		{
			UnityEngine.Debug.LogError("URL and Name are required to add a submodule.");
			return;
		}

		string packagePath = GetPackagePath(name, url);

		string args = $"submodule add -b {branch} {url} {packagePath}";
		string output = RunGitCommand(args, Directory.GetCurrentDirectory());
		UnityEngine.Debug.Log($"Added submodule '{name}' → {packagePath}\n{output}");
	}

	private string GetPackagePath(string name, string url)
	{
		// Case 1: already a valid Unity package id
		if (Regex.IsMatch(name, @"^com\.[a-z0-9]+(\.[a-z0-9]+)+$"))
		{
			return Path.Combine("Packages", name).Replace("\\", "/");
		}

		// Case 2: derive from GitHub username
		string prefix = "com.local"; // fallback
		var match = Regex.Match(url, @"github\.com[:/](?<user>[^/]+)/", RegexOptions.IgnoreCase);
		if (match.Success)
		{
			string user = match.Groups["user"].Value.ToLower();
			prefix = $"com.{user}";
		}

		// Normalize the package name
		string safeName = Regex.Replace(name, @"[^a-zA-Z0-9]", "").ToLower();

		return Path.Combine("Packages", $"{prefix}.{safeName}").Replace("\\", "/");
	}

	// ---------- Remove ----------
	private void RemoveSubmodule(string path)
	{
		string root = Directory.GetCurrentDirectory();

		RunGitCommand($"submodule deinit -f -- {path}", root);
		RunGitCommand($"rm -f {path}", root);

		string moduleMeta = Path.Combine(root, ".git/modules", path.Replace("\\", "/"));
		if (Directory.Exists(moduleMeta))
		{
			Directory.Delete(moduleMeta, true);
		}

		UnityEngine.Debug.Log($"Removed submodule at {path}");
	}

	// ---------- Util ----------
	private string RunGitCommand(string arguments, string workingDir)
	{
		ProcessStartInfo psi = new ProcessStartInfo
		{
			FileName = "git",
			Arguments = arguments,
			WorkingDirectory = workingDir,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};

		using (Process process = Process.Start(psi))
		{
			string output = process.StandardOutput.ReadToEnd();
			string error = process.StandardError.ReadToEnd();
			process.WaitForExit();

			if (!string.IsNullOrEmpty(error))
				UnityEngine.Debug.LogWarning(error);

			return output;
		}
	}

	private class GitSubmodule
	{
		public string Name;
		public string Path;
		public string Url;
		public string CommitHash;
	}
}