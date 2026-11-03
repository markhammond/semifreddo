#!/usr/bin/env bash
# Compiles each escape attempt in isolation and prints the compiler's verdict.
cd "$(dirname "$0")"
for n in 1 2 3 4 5 6; do
  out=$(dotnet build -c Release -p:DefineConstants=CASE_$n -o /tmp/escape_$n 2>&1)
  if grep -q "Build succeeded" <<<"$out"; then echo "CASE_$n: COMPILED (escape NOT prevented!)";
  else echo "CASE_$n: $(grep -oE 'error CS[0-9]+: [^[]+' <<<"$out" | head -1)"; fi
done
