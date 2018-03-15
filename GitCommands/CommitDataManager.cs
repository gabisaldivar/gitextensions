using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using GitUIPluginInterfaces;
using JetBrains.Annotations;

namespace GitCommands
{
    public interface ICommitDataManager
    {
        /// <summary>
        /// Parses <paramref name="data"/> into a <see cref="CommitData"/> object.
        /// </summary>
        /// <param name="data">Data produced by a <c>git log</c> or <c>git show</c> command where <c>--format</c>
        /// was provided the string <see cref="CommitDataManager.LogFormat"/>.</param>
        /// <returns>CommitData object populated with parsed info from git string.</returns>
        [NotNull]
        CommitData CreateFromFormatedData([NotNull] string data);

        /// <summary>
        /// Creates a <see cref="CommitData"/> object from <paramref name="revision"/>.
        /// </summary>
        /// <param name="revision">The commit to return data for.</param>
        [NotNull]
        CommitData CreateFromRevision([NotNull] GitRevision revision);

        /// <summary>
        /// Gets the commit info for submodule.
        /// </summary>
        [ContractAnnotation("=>null,error:notnull")]
        CommitData GetCommitData(string sha1, out string error);

        /// <summary>
        /// Creates a CommitData object from formated commit info data from git.  The string passed in should be
        /// exact output of a log or show command using --format=LogFormat.
        /// </summary>
        /// <param name="data">Formated commit data from git.</param>
        void UpdateBodyInCommitData(CommitData commitData, string data);

        /// <summary>
        /// Updates the <see cref="CommitData.Body"/> property of <paramref name="commitData"/>.
        /// </summary>
        void UpdateCommitMessage(CommitData commitData, [CanBeNull] out string error);
    }

    public sealed class CommitDataManager : ICommitDataManager
    {
        private const string LogFormat = "%H%n%T%n%P%n%aN <%aE>%n%at%n%cN <%cE>%n%ct%n%e%n%B%nNotes:%n%-N";
        private const string ShortLogFormat = "%H%n%e%n%B%nNotes:%n%-N";

        private readonly Func<IGitModule> _getModule;

        public CommitDataManager(Func<IGitModule> getModule)
        {
            _getModule = getModule;
        }

        /// <inheritdoc />
        public void UpdateCommitMessage(CommitData commitData, out string error)
        {
            var module = GetModule();

            // Do not cache this command, since notes can be added
            string arguments = string.Format(CultureInfo.InvariantCulture,
                "log -1 --pretty=\"format:" + ShortLogFormat + "\" {0}", commitData.Guid);
            var info = module.RunGitCmd(arguments, GitModule.LosslessEncoding);

            if (GitModule.IsGitErrorMessage(info) || !info.Contains(commitData.Guid))
            {
                error = "Cannot find commit " + commitData.Guid;
                return;
            }

            UpdateBodyInCommitData(commitData, info);
        }

        /// <inheritdoc />
        public CommitData GetCommitData(string sha1, out string error)
        {
            if (sha1 == null)
            {
                throw new ArgumentNullException(nameof(sha1));
            }

            var module = GetModule();

            // Do not cache this command, since notes can be added
            string arguments = string.Format(CultureInfo.InvariantCulture,
                "log -1 --pretty=\"format:" + LogFormat + "\" {0}", sha1);
            var info = module.RunGitCmd(arguments, GitModule.LosslessEncoding);

            if (GitModule.IsGitErrorMessage(info) || !info.Contains(sha1))
            {
                error = "Cannot find commit " + sha1;
                return null;
            }

            return CreateFromFormatedData(info);
        }

        /// <inheritdoc />
        public CommitData CreateFromFormatedData(string data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            var module = GetModule();

            // $ git log --pretty="format:%H%n%T%n%P%n%aN <%aE>%n%at%n%cN <%cE>%n%ct%n%e%n%B%nNotes:%n%-N" -1
            // 4bc1049fc3b9191dbd390e1ae6885aedd1a4e34b
            // a59c21f0b2e6f43ae89b76a216f9f6124fc359f8
            // 8e3873685d89f8cb543657d1b9e66e516cae7e1d dfd353d3b02d24a0d98855f6a1848c51d9ba4d6b
            // RussKie <RussKie@users.noreply.github.com>
            // 1521115435
            // GitHub <noreply@github.com>
            // 1521115435
            //
            // Merge pull request #4615 from drewnoakes/modernise-3
            //
            // New language features
            // Notes:

            // commit id
            // tree id
            // parent ids (separated by spaces)
            // author
            // authored date (unix time)
            // committer
            // committed date (unix time)
            // encoding (may be blank)
            // diff notes
            // ...

            var lines = data.Split('\n');

            var guid = lines[0];

            // TODO: we can use this to add more relationship info like gitk does if wanted
            var treeGuid = lines[1];

            // TODO: we can use this to add more relationship info like gitk does if wanted
            var parentGuids = lines[2].Split(' ');
            var author = module.ReEncodeStringFromLossless(lines[3]);
            var authorDate = DateTimeUtils.ParseUnixTime(lines[4]);
            var committer = module.ReEncodeStringFromLossless(lines[5]);
            var commitDate = DateTimeUtils.ParseUnixTime(lines[6]);
            var commitEncoding = lines[7];
            var message = ProccessDiffNotes(startIndex: 8, lines);

            // commit message is not reencoded by git when format is given
            var body = module.ReEncodeCommitMessage(message, commitEncoding);

            return new CommitData(guid, treeGuid, parentGuids, author, authorDate, committer, commitDate, body);
        }

        /// <inheritdoc />
        public void UpdateBodyInCommitData(CommitData commitData, string data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            // $ git log --pretty="format:%H%n%e%n%B%nNotes:%n%-N" -1
            // 8c601c9bb040e575af75c9eee6e14441e2a1b207
            //
            // Remove redundant parameter
            //
            // The sha1 parameter must match CommitData.Guid.
            // There's no point passing it. It only creates opportunity for bugs.
            //
            // Notes:

            // commit id
            // encoding
            // commit message
            // ...

            var lines = data.Split('\n');

            var guid = lines[0];
            var commitEncoding = lines[1];
            var message = ProccessDiffNotes(startIndex: 2, lines);

            Debug.Assert(commitData.Guid == guid, "commitData.Guid == guid");

            // commit message is not reencoded by git when format is given
            commitData.Body = GetModule().ReEncodeCommitMessage(message, commitEncoding);
        }

        /// <inheritdoc />
        public CommitData CreateFromRevision(GitRevision revision)
        {
            if (revision == null)
            {
                throw new ArgumentNullException(nameof(revision));
            }

            return new CommitData(revision.Guid, revision.TreeGuid, revision.ParentGuids.ToList().AsReadOnly(),
                string.Format("{0} <{1}>", revision.Author, revision.AuthorEmail), revision.AuthorDate,
                string.Format("{0} <{1}>", revision.Committer, revision.CommitterEmail), revision.CommitDate,
                revision.Body ?? revision.Subject);
        }

        [NotNull]
        private IGitModule GetModule()
        {
            var module = _getModule();

            if (module == null)
            {
                throw new ArgumentException($"Require a valid instance of {nameof(IGitModule)}");
            }

            return module;
        }

        [NotNull]
        private static string ProccessDiffNotes(int startIndex, [NotNull, ItemNotNull] string[] lines)
        {
            int endIndex = lines.Length - 1;
            if (lines[endIndex] == "Notes:")
            {
                endIndex--;
            }

            var message = new StringBuilder();
            bool notesStart = false;

            for (int i = startIndex; i <= endIndex; i++)
            {
                string line = lines[i];

                if (notesStart)
                {
                    message.Append("    ");
                }

                message.AppendLine(line);

                if (line == "Notes:")
                {
                    notesStart = true;
                }
            }

            return message.ToString();
        }
    }
}