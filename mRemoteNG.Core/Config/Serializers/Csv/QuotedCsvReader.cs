using System.Text;

namespace mRemoteNG.Core.Config.Serializers.Csv
{
    /// <summary>
    /// Minimal RFC 4180 reader: fields may be wrapped in double quotes, which allows embedded
    /// separators, line breaks and doubled quotes ("").
    /// </summary>
    internal static class QuotedCsvReader
    {
        public static List<List<string>> ReadRows(string content, char separator = ',')
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;
            var fieldStarted = false;

            for (var i = 0; i < content.Length; i++)
            {
                var c = content[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < content.Length && content[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                    continue;
                }

                if (c == '"' && field.Length == 0)
                {
                    inQuotes = true;
                    fieldStarted = true;
                }
                else if (c == separator)
                {
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = true;
                }
                else if (c is '\r' or '\n')
                {
                    if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                        i++;
                    EndRow();
                }
                else if (c != '﻿' || i != 0)
                {
                    field.Append(c);
                    fieldStarted = true;
                }
            }

            EndRow();
            return rows;

            void EndRow()
            {
                if (fieldStarted || field.Length > 0 || row.Count > 0)
                {
                    row.Add(field.ToString());
                    rows.Add(row);
                }
                row = [];
                field.Clear();
                fieldStarted = false;
            }
        }
    }
}
