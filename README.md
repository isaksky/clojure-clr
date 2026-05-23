# Branch Summary

This branch is primarily interesting for the startup speed change in
`Clojure.Main`. With generated `clojure.*.clj.dll` namespace assemblies copied
into the main output and prepared as ReadyToRun images, the measured Release
`net10.0` startup path is consistently under 500 ms on the tested scripts.

Same-session measurements were taken on 2026-05-23 on macOS 15.7 arm64 with
.NET SDK `10.0.105` and runtime `10.0.5`. Each successful row used one warmup
and three measured fresh processes.

| Startup probe | This branch median / p95 | `master` median / p95 | Change |
| --- | ---: | ---: | ---: |
| `-e "(println :ok)"` | `226.4 ms` / `227.2 ms` | `3133.9 ms` / `3141.7 ms` | `13.8x` faster |
| First `clojure.string` require | `236.0 ms` / `236.2 ms` | `3123.1 ms` / `3126.5 ms` | `13.2x` faster |
| Generated startup feature script | `307.7 ms` / `317.2 ms` | `3311.4 ms` / `3331.1 ms` | `10.8x` faster |
| `samples/stm/teststm.clj` | `268.8 ms` / `270.2 ms` | `3281.8 ms` / `3288.2 ms` | `12.2x` faster |
| `samples/spec_schema.clj` | `319.4 ms` / `323.0 ms` | `4761.2 ms` / `4778.8 ms` | `14.9x` faster |

The useful pattern is simple: the branch starts comparable scripts in roughly
`226-319 ms`, while `master` takes roughly `3.1-4.8 s`.

# ClojureCLR

This project is a native implementation of Clojure on the Common Language Runtime (CLR),
the execution engine of Microsoft's .Net Framework.

ClojureCLR is programmed in C# (and Clojure itself).

## Goals

* Implement a feature-complete Clojure on top of CLR
* Stay as close as possible to the JVM implementation
* Have some fun

## Getting Started

ClojureCLR can either run as a a standalone application, or embedded as a library in .NET applications. See the
[getting started](https://github.com/clojure/clojure-clr/wiki/Getting-started) guide for instructions on how to
install, run or integrate ClojureCLR.

## Documentation

Most of the documentation for [Clojure](https://clojure.org/) should be equally applicable to ClojureCLR. See the
ClojureCLR [wiki]((https://github.com/clojure/clojure-clr/wiki)) and the articles under the [docs](/docs) directory
in this repository for documentation specific to the ClojureCLR project.

## Community and Support

* The [#clr](https://clojurians.slack.com/archives/C060SFCPR) channel in the [Clojurians Slack](https://clojurians.slack.com)
* The [ClojureCLR](https://ask.clojure.org/index.php/clojureclr) category in [Ask Clojure](https://ask.clojure.org/)
* [ClojureCLR JIRA](https://clojure.atlassian.net/jira/software/c/projects/CLJCLR/issues/) is used for issue tracking.
Note that creating issues in the Clojure JIRA requires signing a
[contributor agreement](https://clojure.org/dev/contributor_agreement). Minor issues might be easier to raise on Slack.

## Libraries

Many of the standard libraries from JVM Clojure have [CLR ports](https://github.com/orgs/clojure/repositories?q=clr).

## Other Resources

* [David Miller and Clojure on the CLR](https://zencastr.com/z/oOS93SBX) with [@dmiller](https://github.com/dmiller/)
* [Transform Microsoft Office Solutions into Cloud-savvy Linked Data Microservices With Clojure on .NET](https://www.youtube.com/watch?v=pImaXoTPWWA) with [@bcalco](https://github.com/bcalco/)

## License ##

    Copyright (c) Rich Hickey. All rights reserved. The use and
    distribution terms for this software are covered by the Eclipse
    Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
    which can be found in the file epl-v10.html at the root of this
    distribution. By using this software in any fashion, you are
    agreeing to be bound by the terms of this license. You must
    not remove this notice, or any other, from this software.
