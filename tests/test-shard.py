#!/usr/bin/env python3
"""Prints the `dotnet test --filter` of one shard of the backend tests.

Usage: dotnet test tests/Vellum.Tests --no-build --list-tests | tests/test-shard.py COUNT INDEX

Each test class goes to exactly one shard, so the tests of a class still share one app and
one database. Classes go to the shard with the fewest tests so far, largest class first. The
split depends only on the list of tests, so every shard computes the same split.
"""
import collections
import sys

count, index = int(sys.argv[1]), int(sys.argv[2])
if not 0 <= index < count:
    sys.exit(f"index {index} is not in 0..{count - 1}")

listed = False
classes = collections.Counter()
for line in sys.stdin:
    if line.startswith("The following Tests are available:"):
        listed = True
    elif listed and line.startswith("    "):
        name = line.strip().split("(", 1)[0]
        classes[name.rsplit(".", 1)[0]] += 1
if not classes:
    sys.exit("no tests in the input")

load = [0] * count
shards = [[] for _ in range(count)]
for name, tests in sorted(classes.items(), key=lambda c: (-c[1], c[0])):
    smallest = load.index(min(load))
    load[smallest] += tests
    shards[smallest].append(name)

# The trailing dot keeps a class from matching another class whose name starts with its name.
print("Category!=Slow&(" + "|".join(f"FullyQualifiedName~{name}." for name in sorted(shards[index])) + ")")
