using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Octokit;
using System.Linq;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Net;

namespace prmonitor {
	partial class Program {
		const string org = "dotnet";
		const string repo = "runtime";

		static void Main (string[] args)
		{
			PopulateLeadsArea ().Wait ();

			GitHubClient client = new GitHubClient (new ProductHeaderValue ("Octokit.Samples"));
			client.Credentials = CreateCredentials ();

			var request = new PullRequestRequest () {
				State = ItemStateFilter.Open,
				Base = "main"
			};

			var prs = client.PullRequest.GetAllForRepository (org, repo, request).Result;

			var rl = client.GetLastApiInfo ().RateLimit;
			Console.WriteLine ($"Remaining GH api limit {rl.Remaining} will reset at {rl.Reset.ToLocalTime ()}");

			DateTime cutDate = DateTime.Today.AddDays (-21);

			int drafts = 0;
			int active = 0;
			var inactive = new List<(PullRequest, DateTime)> ();

			foreach (PullRequest pr in prs) {
				if (pr.Draft) {
					++drafts;
					continue;
				}

				if (pr.Labels.Any (l => l.Name == "needs-author-action"))
					continue;

				if (!pr.Labels.Any (l => l.Name == "community-contribution"))
					continue;

				if (IsActivePR (client, pr, cutDate, out DateTime lastActivity)) {
					++active;
					continue;
				}

				inactive.Add ((pr, lastActivity));
			}

			StringWriter sw = new StringWriter ();
			ReportInactivePRs (inactive, sw, client);

			var res = typeof (Program).Assembly.GetManifestResourceStream ("prmonitor.output.html.template");

			using (var input = new StreamReader (res!, Encoding.UTF8)) {
				var text = input.ReadToEnd ().Replace ("##BODY##", sw.ToString ()).Replace ("##DATE##", DateTime.Today.ToString ("dd MMMM yyyy"));
				File.WriteAllText ("output.html", text);
			}

			return;
		}

		static bool IsActivePR (GitHubClient client, PullRequest pr, DateTime activityDate, out DateTime lastActivity)
		{
			// Was the PR created recently
			if (pr.CreatedAt > activityDate) {
				lastActivity = pr.CreatedAt.Date;
				return true;
			}

			// Is the latest commit date newer
			var last_commit = client.Repository.Commit.Get (org, repo, pr.Head.Sha).Result;
			var last_commit_date = last_commit.Commit.Committer.Date.Date;
			if (last_commit_date > activityDate) {
				lastActivity = last_commit_date;
				return true;
			}

			// Is the latest review comment date newer
			var review = client.PullRequest.ReviewComment.GetAll (org, repo, pr.Number).Result;
			if (review.Any (l => l.CreatedAt > activityDate)) {
				lastActivity = DateTime.MaxValue;
				return true;
			}

			// Is the latest PR comment date newer
			var comments = client.Issue.Comment.GetAllForIssue (org, repo, pr.Number).Result.Where (l => IsRelevantComment (l));
			if (comments.Any (l => l.CreatedAt > activityDate)) {
				lastActivity = DateTime.MaxValue;
				return true;
			}

			lastActivity = comments.Select (l => l.CreatedAt.Date).Concat (review.Select (l => l.CreatedAt.Date)).DefaultIfEmpty (DateTime.MinValue).Max ();
			if (last_commit_date > lastActivity)
				lastActivity = last_commit_date;

			return false;

			static bool IsRelevantComment (IssueComment comment)
			{
				var msg = comment.Body;

				if (msg.Contains ("PTAL") || msg.Contains ("CLA assistant ") || msg.Contains ("new-api-needs-documentation") ||
					msg.StartsWith ("@") || msg.StartsWith ("Tagging ") || msg.StartsWith ("cc") || msg.StartsWith ("ping "))
					return false;

				return true;
			}
		}

		static void ReportInactivePRs (List<(PullRequest, DateTime)> pullRequests, StringWriter sw, GitHubClient client)
		{
			Dictionary<PullRequest, string?> pr_scope = new Dictionary<PullRequest, string?> ();
			foreach (var item in pullRequests) {
				var pr = item.Item1;
				pr_scope.Add (pr, GetScope (pr));
			}

			Dictionary<PullRequest, string?> pr_owner = new Dictionary<PullRequest, string?> ();
			foreach (var item in pullRequests) {
				var pr = item.Item1;
				var scope = pr_scope[pr];
				if (scope is null)
					continue;

				/*
				// Prefer Assignee over area lead
				var assignee = pr.Assignees.FirstOrDefault ()?.Login;
				if (assignee is not null) {
					var mun = GetMicrosoftUserName (assignee, client);
					if (mun is not null) {
						pr_owner.Add (pr, mun);
						continue;
					}
				}
				*/
				pr_owner.Add (pr, GetScopeLead (scope));
			}

			//
			// Group by Area, then by largest count in the area, then date
			//
			var grouping = pullRequests.
				Where (l => pr_owner.ContainsKey (l.Item1)).
				GroupBy (l => pr_owner[l.Item1]).
				Select (l => new {
					Lead = l.Key,
					Items = l.OrderBy (ll => ll.Item2).ToList ()
				}).
				OrderByDescending (id => id.Items.Count ()).
				ThenBy (id => id.Items.First ().Item2);

			foreach (var group in grouping) {
				sw.WriteLine ($"<p>{WebUtility.HtmlEncode (group.Lead)}</p>");

				sw.WriteLine ("<table>");
				sw.WriteLine ("<thead><tr><th>Pull Request</th><th>Assignee</th><th>Scope</th><th>Stale Days</th></thead>");
				sw.WriteLine ("<tbody>");

				foreach (var item in group.Items) {
					var pr = item.Item1;
					sw.WriteLine ("<tr>");
					sw.WriteLine ($"<td class=\"c1\"><a href=\"{ pr.HtmlUrl }\">{WebUtility.HtmlEncode (pr.Title.Trim ())}</a></td>");
					sw.WriteLine ($"<td class=\"c2\">{ WebUtility.HtmlEncode (GetMicrosoftUserName (pr.Assignees.FirstOrDefault ()?.Login, client))}</td>");
					var scope = pr_scope[pr];
					sw.WriteLine ($"<td class=\"c3\">{ WebUtility.HtmlEncode (scope?.Substring (scope.IndexOf ('-') + 1))}</td>");
					sw.WriteLine ($"<td class=\"c4\">{ (DateTime.Today - item.Item2).TotalDays}</td>");
					sw.WriteLine ("</tr>");
				}

				sw.WriteLine ("</tbody>");
				sw.WriteLine ("</table>");
			}
		}

		static string? GetScope (PullRequest pullRequest)
		{
			string? scope = FindLabel ("arch") ?? FindLabel ("os") ?? FindLabel ("area");
			if (scope is null) {
				Console.WriteLine ($"PR {pullRequest.HtmlUrl} is missing scope label");
			}

			return scope;

			string? FindLabel (string prefix)
			{
				string? label = null;
				foreach (var l in pullRequest.Labels) {
					if (!l.Name.StartsWith (prefix + "-", StringComparison.InvariantCultureIgnoreCase))
						continue;

					if (label != null) {
						Console.WriteLine ($"PR {pullRequest.HtmlUrl} has multiple {prefix} labels");
						continue;
					}

					if (!leadsCache.ContainsKey (l.Name))
						continue;

					label = l.Name;
				}

				return label;
			}
		}

		static Dictionary<string, string> leadsCache = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase) {
			{ "arch-loongarch64", "@mangod9" }, // TODO: Who should that be?
			{ "os-linux", "@jeffhandley" }, // TODO: Who should that be?
			{ "arch-riscv", "@JulieLeeMSFT" }, // TODO: Update in .md file
		};

		static Dictionary<string, string> leadsNames = new Dictionary<string, string> (StringComparer.OrdinalIgnoreCase) {
			{ "@agocke", "Andy Gocke" },
			{ "@sammonort", "Sam Patel" },
			{ "@ericstj", "Eric St. John" },
			{ "@karelz", "Karel Zikmund" },
			{ "@steveisok", "Steve Pfister" },
			{ "@lewing", "Larry Ewing" },
			{ "@jeffhandley", "Jeff Handley" },
			{ "@JulieLeeMSFT", "Julie Lee" },
			{ "@jeffschwMSFT", "Jeff Schwartz" },
			{ "@tommcdon", "Tom McDonald" },
			{ "@mangod9", "Manish Godse" },
			{ "@dleeapho", "Dan Leeaphon" },
			{ "@HongGit", "Hong Li" },
			{ "@marek-safar", "Marek Safar" },
			{ "@kevinpi", "Kevin Pilch"},
			{ "@jaredpar", "Jared Parsons"},
			{ "@ajcvickers", "Arthur Vickers" },
			{ "@danmoseley", "Dan Moseley"},
			{ "@MichaelSimons", "Michael Simons"},
			{ "@adityamandaleeka", "Aditya Mandaleeka"},
			{ "@David-Engel", "David Engel"},
		};

		static async Task PopulateLeadsArea ()
		{
			var http = new HttpClient ();
			var data = await http.GetStringAsync ($"https://raw.githubusercontent.com/{org}/{repo}/main/docs/area-owners.md");
			PopulateLeadsCache ("area-");
			PopulateLeadsCache ("arch-");
			PopulateLeadsCache ("os-");

			void PopulateLeadsCache (string prefix)
			{
				bool first = true;
				foreach (var line in data.Split ("| " + prefix)) {
					if (first) {
						first = false;
						continue;
					}

					var area_data = line.Split ('|');
					if (area_data.Length < 2) {
						Console.WriteLine ("Unexpected leads format");
						continue;
					}

					var area = prefix + area_data[0].Trim ();
					var lead = area_data[1].Trim ();
					if (leadsCache.ContainsKey (area)) {
						Console.WriteLine ($"Duplicate area lead for '{area}'");
						continue;
					}

					leadsCache.Add (area, lead);
				}
			}
		}

		static string GetScopeLead (string scope)
		{
			if (!leadsCache.TryGetValue (scope, out var lead)) {
				Console.WriteLine ("Missing lead for " + scope);
				return "Unknown";
			}

			if (leadsNames.TryGetValue (lead, out var name))
				return name;

			Console.WriteLine ($"Missing lead alias mapping for {lead}");
			return "??";
		}

		static string? GetMicrosoftUserName (string? login, GitHubClient client)
		{
			if (login is null)
				return null;

			var un = client.User.Get (login).Result;
			return un.Name;
		}
	}
}
