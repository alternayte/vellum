#!/usr/bin/env bash
# Every module under src/Vellum/Modules follows one layout:
#   <Module>Module.cs      the entry point (services, endpoints, streams, projections)
#   <Module>Tables.cs      entities and their EF configuration, when the module has tables
#   <Aggregate>Events.cs, <Aggregate>State.cs, <Aggregate>Decider.cs, <Aggregate>Projection.cs
#                          for each event-sourced aggregate (each `.Stream<XState>` registration)
# A file under the threshold is a per-operation fragment: group it with its feature.
# The files the layout names are exempt from the size rule.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
modules="$root/src/Vellum/Modules"
min_lines=20
failed=0

fail() {
    echo "module-layout: $1" >&2
    failed=1
}

layout_files=""

for dir in "$modules"/*/; do
    module="$(basename "$dir")"
    [ -f "$dir/${module}Module.cs" ] || fail "$module has no ${module}Module.cs"
    layout_files="$layout_files ${module}/${module}Module.cs"

    if grep -rqs --include='*.cs' 'ToTable(' "$dir"; then
        [ -f "$dir/${module}Tables.cs" ] || fail "$module has tables but no ${module}Tables.cs"
        layout_files="$layout_files ${module}/${module}Tables.cs"
    fi

    if [ -d "$dir/Entities" ]; then
        fail "$module has an Entities folder: entities go in ${module}Tables.cs"
    fi

    # Each `.Stream<XState>(` registration is an event-sourced aggregate X.
    for aggregate in $(grep -rhos --include='*.cs' '\.Stream<[A-Za-z0-9_]*State>' "$dir" | sed -E 's/\.Stream<([A-Za-z0-9_]*)State>/\1/' | sort -u); do
        for part in Events State Decider Projection; do
            file="${aggregate}${part}.cs"
            [ -f "$dir/$file" ] || fail "$module registers the stream of $aggregate but has no $file"
            layout_files="$layout_files ${module}/${file}"
        done
    done
done

while IFS= read -r file; do
    relative="${file#"$modules"/}"
    case " $layout_files " in
        *" $relative "*) continue ;;
    esac
    lines=$(wc -l < "$file" | tr -d ' ')
    if [ "$lines" -lt "$min_lines" ]; then
        fail "$relative has $lines lines (fewer than $min_lines): group it with its feature"
    fi
done < <(find "$modules" -name '*.cs' -not -path '*/Migrations/*' | sort)

if [ "$failed" -ne 0 ]; then
    exit 1
fi
echo "module-layout: ok"
