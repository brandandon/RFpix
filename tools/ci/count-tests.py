"""count-tests.py - for the GitHub build: how many tests ran and passed, and any that failed,
shown at the top of the run's page.

    python3 tools/ci/count-tests.py "<machine>" [results folder]

Reads the test results (.trx files) that `dotnet test --logger trx` wrote in the results folder
(test-results unless given), and prints one GitHub notice with the counts, plus one error for each
failed test with its message. Prints nothing if there are no results (the tests didn't get to run).
"""

import glob
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def escape(text):
    """GitHub's notices are one line: newlines and % have to be written specially."""
    return text.replace("%", "%25").replace("\r", "").replace("\n", "%0A")


def main():
    machine = sys.argv[1] if len(sys.argv) > 1 else "Tests"
    folder = sys.argv[2] if len(sys.argv) > 2 else "test-results"
    for path in glob.glob(f"{folder}/**/*.trx", recursive=True):
        root = ET.parse(path).getroot()
        counters = root.find("t:ResultSummary/t:Counters", NS)
        results = list(root.iterfind(".//t:UnitTestResult", NS))
        layout = sum(1 for r in results
                     if "FitToWindowTests" in (r.get("testName") or "") and r.get("outcome") == "Passed")
        extra = f" ({layout} screen-layout tests)" if layout else ""
        print(f"::notice title={machine} tests::{counters.get('passed')} of {counters.get('total')} passed{extra}")
        for r in results:
            if r.get("outcome") != "Failed":
                continue
            message = r.find(".//t:Message", NS)
            text = (message.text or "") if message is not None else ""
            print(f"::error title={machine} test failed: {r.get('testName')}::{escape(text.strip())}")


if __name__ == "__main__":
    main()
