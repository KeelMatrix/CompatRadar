# Commit checks

This directory contains optional local Git hooks and the history check used by the repository workflow. They are maintainer checks only and are not part of package installation or normal consumer use.

Run `git config core.hooksPath .githooks` once per clone to enable the repository's local commit checks.

The versioned checks reject identity trailers and internal metadata in new commit messages. The public repository workflow checks the commits introduced by each push or pull request.
