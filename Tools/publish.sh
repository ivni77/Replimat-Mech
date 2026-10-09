#!/bin/sh
# Публичный репо github.com/ivni77/Replimat-Mech (remote public): ветка public — снимок закоммиченного main без Docs и CLAUDE.md,
# своей линией коммитов от noreply-адреса GitHub. Рабочая история main (почта, заметки) остаётся только локально.
set -e
cd "$(dirname "$0")/.."
GIT_INDEX_FILE=.git/public-index git read-tree HEAD
GIT_INDEX_FILE=.git/public-index git rm -r -q --cached --ignore-unmatch Docs CLAUDE.md
tree=$(GIT_INDEX_FILE=.git/public-index git write-tree)
rm -f .git/public-index
parent=$(git rev-parse -q --verify refs/heads/public || true)
if [ -n "$parent" ] && [ "$(git rev-parse "$parent^{tree}")" = "$tree" ]; then
  echo "public: без изменений"
else
  ver=$(sed -n 's|.*<modVersion>\(.*\)</modVersion>.*|\1|p' About/About.xml)
  export GIT_AUTHOR_NAME=ivni77 GIT_AUTHOR_EMAIL=126690933+ivni77@users.noreply.github.com
  export GIT_COMMITTER_NAME=ivni77 GIT_COMMITTER_EMAIL=126690933+ivni77@users.noreply.github.com
  commit=$(printf '%s\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>\n' "${1:-Replimat Mech $ver}" | git commit-tree "$tree" ${parent:+-p "$parent"})
  git update-ref refs/heads/public "$commit"
fi
git push public refs/heads/public:refs/heads/main
