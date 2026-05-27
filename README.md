# Branch Summary

This branch is primarily interesting for the startup speed change in
`Clojure.Main`. With generated `clojure.*.clj.dll` namespace assemblies copied
into the main output and prepared as ReadyToRun images, the measured Release
`net10.0` command-line startup path is consistently under 500 ms on the tested
scripts.

The measurements below were taken on 2026-05-26 MDT on macOS 15.7.3 arm64 with
.NET SDK `10.0.107` and runtime `10.0.7`, against this branch at `fd5af306`.
Each row runs `dotnet Clojure/Clojure.Main/bin/Release/net10.0/Clojure.Main.dll`
in fresh processes with one warmup and three measured runs. The output contained
48 generated `clojure.*.clj/c.dll` namespace assemblies, prepared in place with
`research/scripts/readytorun-generated-clj-dlls.zsh`.

| Startup probe | Median / p95 |
| --- | ---: |
| `-e "(println :ok)"` | `175.7 ms` / `179.4 ms` |
| Macro definition and expansion | `215.6 ms` / `215.8 ms` |
| Destructuring expression | `249.7 ms` / `254.1 ms` |
| Protocol plus `deftype` | `235.7 ms` / `237.0 ms` |
| Multimethod dispatch | `214.4 ms` / `214.7 ms` |
| Lazy seq realization | `180.3 ms` / `182.3 ms` |
| First `clojure.string` require | `192.4 ms` / `199.6 ms` |
| `clojure.spec.alpha` validation | `212.5 ms` / `213.8 ms` |
| `clojure.spec.test.alpha` instrumentation | `211.7 ms` / `213.4 ms` |
| Generated startup feature script | `243.2 ms` / `244.7 ms` |
| `samples/stm/teststm.clj` | `211.8 ms` / `227.0 ms` |
| `samples/spec_schema.clj` | `248.5 ms` / `252.2 ms` |
| `samples/newtonsoft_demo.cljr` | `277.7 ms` / `284.8 ms` |
| `samples/sqlite_demo.cljr` | `249.9 ms` / `257.2 ms` |

The observed branch range for these direct `clojure.main` command-line probes
was `175.6-284.8 ms`; the slowest measured run remained well under the 500 ms
startup gate.

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
