# PR Monitor

This project started as a clone of the code in https://github.com/marek-safar/prmonitor repo.
Some features didn't make sense to have , so those have been removed, as well as few other convenience things have been added.

## Report
The report is generated as an `.html` file and includes the following information:
- Information about number of merged and closed PRs per team member
- Information about number of issues that got `help-wanted` label added
- Community PR report

The Community PR report, represents the list of PRs in a dotnet/aspnetcore repo, which meet the following criteria:
- PRs are open
- have a `community-contribution` label
- Are not in `draft` state
- The last commit in the PR is older than 14 days.

## How to run the tool
After building the tool, run the executable by passing in a GitHub Personal Access Token as the only parameter.
That token will need to have a read access to the repo the reported to be generated for (dotnet/aspnetcore currently).
