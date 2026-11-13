import sys

from .cli import COMMANDS, main


def entry() -> int:
    argv = sys.argv[1:]
    # Файлы из файлового менеджера (.desktop %F) открываем сразу в GUI
    if argv and argv[0] not in COMMANDS and not argv[0].startswith("-"):
        argv = ["gui", *argv]
    return main(argv)


if __name__ == "__main__":
    sys.exit(entry())
