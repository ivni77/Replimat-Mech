#!/bin/sh
# Чистая копия мода для Steam Workshop: только то, что нужно игре (без Source, Docs, Workshop, .git, CLAUDE.md).
# Игра выгружает в Steam всю папку мода, поэтому Mods/Replimat Mech смотрит сюда, а не на проект.
set -e
cd "$(dirname "$0")/.."
out="Release/Replimat Mech"
# id предмета Steam игра пишет в копию при первой выгрузке — забрать в проект, чтобы обновления шли в тот же предмет
[ -f "$out/About/PublishedFileId.txt" ] && cp "$out/About/PublishedFileId.txt" About/
mkdir -p "$out"
# системный rsync с обеих сторон: rsync 3.5.1 из Homebrew (2026-10-09) не заходит в папку внутри «Документов» — Operation not permitted
/usr/bin/rsync -a --delete --exclude .DS_Store --rsync-path=/usr/bin/rsync About Assemblies Defs Languages Patches Sounds Textures LICENSE.md "$out/"
echo "$out: $(du -sh "$out" | cut -f1)"
