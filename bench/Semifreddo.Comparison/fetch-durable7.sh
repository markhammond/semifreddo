#!/bin/sh
# Durable7 (MIT-0, github.com/VladimirReshetnikov/Durable7) is not on NuGet: clone it into external/ at the commit
# these benchmarks were written against. Re-run to return to that commit.
set -eu
cd "$(dirname "$0")"
commit=6ca5902dba08d1b8eab5892274f8fcdf875ca73c
if [ ! -d external/Durable7/.git ]; then
  git clone --filter=blob:none https://github.com/VladimirReshetnikov/Durable7.git external/Durable7
fi
git -C external/Durable7 checkout -q "$commit"
echo "Durable7 at $(git -C external/Durable7 rev-parse --short HEAD)"
