#!/bin/bash
# Analyze DLLs with ilspycmd
# Usage: ./analyze_il.sh [original_dll_path] [modified_dll_path]

if [ -z "$1" ]; then
  echo "Error: Missing original DLL path"
  echo "Usage: ./analyze_il.sh [original_dll_path] [modified_dll_path]"
  exit 1
fi

ORIGINAL_DLL="$1"
MODIFIED_DLL="$2"

echo "Analyzing original DLL: $ORIGINAL_DLL"
ilspycmd "$ORIGINAL_DLL" > original.il

if [ ! -z "$MODIFIED_DLL" ]; then
  echo "Analyzing modified DLL: $MODIFIED_DLL"
  ilspycmd "$MODIFIED_DLL" > modified.il
  
  echo "Comparing IL differences..."
  diff -u original.il modified.il > il_diff.txt
  
  if [ $? -eq 0 ]; then
    echo "No differences found between original and modified assemblies."
  else
    echo "Differences found. See il_diff.txt for details."
  fi
fi

echo "Done. Check original.il for decompiled IL." 